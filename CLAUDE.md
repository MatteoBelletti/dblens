# CLAUDE.md — dblens

dblens is a local MCP server in .NET 10 that inspects and diagnoses PostgreSQL and SQL Server
databases. It never writes to a database and returns no row data — except `query_with_receipt`, which
is disabled unless the operator sets `DbLens:AllowQueryResults`. Do not add another path that returns
values from the data.

Read first: [docs/architecture.md](docs/architecture.md) (layout, data flow, verification pipeline),
[docs/safety.md](docs/safety.md) (what may execute), [docs/tools.md](docs/tools.md) (every tool and
rule), [GLOSSARY.md](GLOSSARY.md) (terms). Keep those in sync with any change of behaviour.

## Build and test

```bash
docker compose up -d   # or: make db — PostgreSQL 5433, SQL Server 1434
dotnet build
dotnet test            # integration tests hit both real databases
```

Run the server: `dotnet run --project src/DbLens.Mcp` (stdio). `MCP_TRANSPORT=sse` serves
`http://localhost:5050/mcp` for MCP Inspector; `make dev` / `make dev-server` add auto-rebuild.
Logging goes to **stderr** only: stdout belongs to the MCP protocol.

Demo volumes hold demo data only; after editing `db/*/0*.sql` recreate them with
`docker compose down -v && docker compose up -d`.

## Where code goes

| Layer | Put here |
|---|---|
| `DbLens.Core/Analysis/<Feature>/` | request/result records and a **pure static engine** — no DB access |
| `DbLens.Core/Inspection/DatabaseInspectorBase*.cs` | orchestration, anything writable without dialect SQL |
| `DbLens.Postgres` / `DbLens.SqlServer` (partial classes) | catalog SQL and dialect hooks only |
| `DbLens.Mcp/Tools/` | thin tools deriving from `InspectorTool` |

Logic goes in the base whenever it can be written without dialect SQL: a rule enforced there cannot
be forgotten by one dialect, and the dialects cannot drift apart.

### Adding an inspector capability

1. Add the method to `IDatabaseInspector`.
2. Add models and a pure engine under `Analysis/<Feature>/`; rule IDs as constants in `RuleIds.cs`.
3. Implement it once in `DatabaseInspectorBase`, wrapped in `RunAsync`. If it needs dialect SQL,
   declare an `abstract` hook and implement it in **both** inspectors.
4. Tests: engine tests with `TestSchema`; integration tests once in an abstract
   `<Feature>TestsBase(IDatabaseFixture)` derived per dialect; a `<Feature>ParityTests.cs`.
5. Add `throw new NotSupportedException()` to the stub inspectors in `GetSchemaToolTests` and
   `GetTableDetailsToolTests`.
6. Document it in `docs/tools.md`.

### Adding a tool

```csharp
[McpServerToolType]
public sealed class MyTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description("What it does. IMPORTANT: what the response does NOT contain. Do NOT infer …")]
    public Task<MyResult> MyMethod(
        [Description("Database dialect: PostgreSql or SqlServer.")]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("Connection string. Omit to use the server's default for the dialect.")]
        string? connectionString = null,
        CancellationToken cancellationToken = default) =>
        ResolveInspector(dialect, connectionString).MyMethodAsync(new MyRequest(), cancellationToken);
}
```

Tools are discovered automatically. Throw `ArgumentException` for bad input and
`ExecutionRefusedException` for a deliberate refusal: `ToolErrors` shows those messages to the model,
while the SDK hides every other exception's text. Keep descriptions short: what the tool finds, then `IMPORTANT:`
lines for what is **not** returned and what executes. Expose only parameters a model has a reason to
set — thresholds stay as request defaults, out of the MCP schema. `[Description]` values must be
compile-time constants.

## Product contract

### `Finding` — do not change its shape without an explicit decision

```csharp
public sealed record Finding(
    string RuleId, Severity Severity, FindingCategory Category,
    double Confidence,                 // 0.0–1.0, within its evidence class
    Evidence Evidence,                 // required, no default: Measured / Derived / Inferred
    string Table,                      // "schema.table"
    IReadOnlyList<string>? Columns,
    string Title, string Description,
    SqlProposal? SqlFix,
    IReadOnlyDictionary<string, object>? Metadata);
```

- Every result has **`Findings`** (Measured + Derived) and **`Signals`** (Inferred) as separate lists.
  Build them with `FindingSplit.Partition`; always-present summary findings go through `WithMandatory`
  so the 200-finding cap never drops them.
- `Summary.BySeverity` / `ByRule` always contain every key (`FindingSplit.BySeverity()` / `ByRule()`).
- `scan_gdpr` returns only signals. `analyze_denormalization` and `infer_relationships` return only
  signals unless verification is on.
- Heuristics are `Evidence.Inferred` with `Confidence < 1.0`, and the tool description says so.
- A detection tool whose empty result could read as an all-clear must report its blind spots
  (`scan_gdpr` → `Coverage`).
- `SqlFix` is a `SqlProposal`: `Ddl` (changes the database) or `ReadOnlyProbe` (aggregate `SELECT`).
  `SafeToExecuteAutomatically` is computed from the kind.
- `demo_good` acceptance: every diagnostic tool scoped to it returns nothing but its summary finding.

### Signal verification

Shared types in `Analysis/Verification/`; flow in docs/architecture.md. Non-negotiable:

- Probes are **aggregate only**: two counts, never a row value.
- A measurement promotes only what it measured. The title states the count; the diagnosis goes under
  `WHAT THIS MAY MEAN:`.
- `Denominator == 0` is `Inconclusive`, never `Refuted`. A refuted signal is a fact at `Info`.
- Only the COUNT family is verifiable (`ColumnNameVocabulary.AggregateFamily`); a SUM's source column
  is not in the catalog.
- Rule IDs never fork on verification state — it lives in the list, `Evidence` and
  `Metadata["verification"]`.
- One rolled-back transaction per probe (a failed statement poisons a PostgreSQL transaction).
- A probe failure never propagates: the unverified analysis is still returned.

### Executing caller SQL

`explain_query` with `analyze = true` and `query_with_receipt` run the caller's SQL. Three layers,
all in `DatabaseInspectorBase`, none skippable by a dialect:

1. `StatementGuard` before any connection opens — a filter (keywords, side-effecting functions,
   locking clauses, linked servers), not a proof. **Never relax it to make a query work**; extend it
   when a new side effect is found, with a test in `StatementGuardSideEffectTests`. It reads the
   text through `SqlText.Segments`, which must end every string and comment where the engine does
   (`StatementGuardLexingTests`); never scan caller SQL with a regex instead. An estimated plan
   skips the keyword filter but not `IsSingleStatement`.
2. A transaction that is always rolled back (`READ ONLY` on PostgreSQL).
3. `EnsureReadOnlyLoginAsync`: refuse when the login can write (`DescribeWriteCapabilityAsync`) —
   unless every server in the connection string is this machine (`LocalServer.IsLocal`, address only,
   no DNS). `DbLens:ReadOnlyLoginOnLocalhost` re-applies it locally (dblens on a real DB server);
   `DbLens:AllowWriteCapableLogin` disables it, for disposable databases only.

Probes are dblens's own SQL and skip layer 3, never layers 1 and 2.

## Conventions

- `net10.0`, C# 14, nullable, warnings as errors, central package versions in `Directory.Packages.props`.
- No comments unless the reason is non-obvious.
- PostgreSQL and SQL Server stay at feature parity. Genuine differences are asserted as differences
  (SQL Server rejects a self-referencing `ON DELETE CASCADE`, Msg 1785, so its demo `categories` is
  `NO ACTION`).
- Integration tests hit real databases — never mock the data layer — and **skip, never pass**, when
  one is unreachable: `[SkippableFact]` + `Skip.If(ShouldSkip, TestSkip.Reason)`, never
  `if (ShouldSkip) return;`. CI fails on any skipped test.
- Test connection strings come from `DBLENS_TEST_PG_CONNECTION` / `DBLENS_TEST_SS_CONNECTION` (the
  read-only `dblens_reader`) and `DBLENS_TEST_*_ADMIN_CONNECTION` (setup and refusal tests only), with
  hardcoded fallbacks.
- Integration tests that count rows must not touch `orders` on SQL Server: `COUNT(*)` is answered by
  scanning `idx_orders_status`, which the unused-index test needs unscanned.
- When one tool emits SQL another tool reads (DDL, probes), add a case to `CompositionTestsBase`.
- Every command sets `CommandTimeout = QueryTimeoutSeconds` (30, in `DatabaseInspectorBase`).
  Don't catch `TimeoutException`: drivers raise `NpgsqlException` / `SqlException`.
- Generated SQL: `SqlIdentifier.Quote` / `Qualify` for every catalog name, `IndexSql` for index DDL.
  Never interpolate an identifier unquoted — quoting also keeps names inert to `StatementGuard`.
- Counts from a reader: `Convert.ToInt64(reader.GetValue(i))`, never `GetInt64` (SQL Server `COUNT`
  is `int`). Generated SQL Server probes use `COUNT_BIG`.
- Finding text is a locale-independent contract: `Num.N0` / `F1` / `F0` / `P0`, never `:N0`.
- Rule IDs and limits by constant: `*RuleIds`, `DiagnosticLimits.MaxTablesPerRequest` /
  `MaxFindingCount`. Never inline.
- After `GetTableDetailsAsync`, append `NotFoundCaveat(details)` via `WithCaveat`.
- A table is a `(schema, name)` pair from the catalog. Never join one into `"schema.name"` and split
  it again: names may contain dots. Only a name the caller typed goes through `ParseTableNames`.
- A catalog shape the demo schema lacks (partitions, odd names, index kinds) gets a table in the
  `dblens_edge` fixtures and a test in `EdgeCatalogTestsBase`, not a new demo table.
- Summary findings (`*-summary`) are exactly one, `Info`, never deduplicated.

## Defaults

```
PostgreSQL  Host=localhost;Port=5433;Database=dblens_demo;Username=dblens_reader;Password=readerpassword
SQL Server  Server=localhost,1434;Database=dblens_demo;User Id=dblens_reader;Password=ReaderPassword123!;TrustServerCertificate=true
```

In `appsettings.json`, with the same values as fallbacks in `Program.cs`; an empty value (an unset
`.mcpb` setting) means the default. The admin logins (`postgres` / `sa`) exist for setup only.
Non-standard ports avoid clashing with local installs.

## Releasing

Bump `<Version>` in `src/DbLens.Mcp/DbLens.Mcp.csproj` and both versions in
`src/DbLens.Mcp/.mcp/server.json`, then push a `v<version>` tag; the release workflow builds binaries,
`.mcpb` extensions and the NuGet package, and publishes it via Trusted Publishing (OIDC, no stored
key). Never add a long-lived NuGet API key. See docs/architecture.md#releases.
