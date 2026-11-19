# CLAUDE.md — dblens

dblens is a local MCP server in .NET 10 that inspects and diagnoses PostgreSQL and SQL Server
databases. It never writes to a database and returns no row data — except `query_with_receipt`, which
is disabled unless the operator sets `DbLens:AllowQueryResults`, and the migration ids
`compare_ef_model` reads from EF Core's history table (docs/safety.md). Do not add another path that
returns values from the data. Workload SQL leaves only through `SqlText.Redact` (`find_hot_queries`).

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
| `DbLens.EfCore` | reading EF Core snapshots as text (Roslyn); referenced by `DbLens.Mcp` only |
| `DbLens.Mcp/Tools/` | thin tools deriving from `InspectorTool` |
| `plugins/dblens/` | the optional Claude Code plugin: agents, the `audit` workflow, skills, evals ([decisions](docs/roadmap/decisions.md)) — prompts only, no logic the server lacks |

Logic goes in the base whenever it can be written without dialect SQL: a rule enforced there cannot
be forgotten by one dialect, and the dialects cannot drift apart.

### Adding an inspector capability

1. Add the method to `IDatabaseInspector`.
2. Add models and a pure engine under `Analysis/<Feature>/`; rule IDs as constants in `RuleIds.cs`.
3. Implement it once in `DatabaseInspectorBase`, wrapped in `RunAsync`. If it needs dialect SQL,
   declare an `abstract` hook and implement it in **both** inspectors — ideally one that returns a
   command or rows, with the same column aliases on both, so the reading stays in the base.
4. Tests: engine tests with `TestSchema`; integration tests once in an abstract
   `<Feature>TestsBase(IDatabaseFixture)` derived per dialect; a `<Feature>ParityTests.cs`.
5. Add its `throw new NotSupportedException()` default to `StubInspector` (tests), the one stub
   the tool tests share.
6. Document it in `docs/tools.md`.

### Adding a tool

```csharp
[McpServerToolType]
public sealed class MyTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description("What it does. IMPORTANT: what the response does NOT contain. Do NOT infer …")]
    public Task<MyResult> MyMethod(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        CancellationToken cancellationToken = default) =>
        ResolveInspector(dialect, connectionString).MyMethodAsync(new MyRequest(), cancellationToken);
}
```

Tools are discovered automatically. Their names and input schemas are a contract with every client:
`ToolSurfaceSnapshotTests` fails on a tool or parameter removed, retyped or newly required, and an
addition is recorded by running the tests once with `DBLENS_UPDATE_TOOL_SURFACE=1`. Throw `ArgumentException` for bad input and
`ExecutionRefusedException` for a deliberate refusal: `ToolErrors` shows those messages to the model,
while the SDK hides every other exception's text. Keep descriptions short: what the tool finds, then `IMPORTANT:`
lines for what is **not** returned and what executes. Expose only parameters a model has a reason to
set — thresholds stay as request defaults, out of the MCP schema. `[Description]` values must be
compile-time constants; the ones every tool shares (`DialectHelp`, `SchemaHelp`, `TablesHelp`…) are
in `InspectorTool`.

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
- Outcome metadata through `VerificationOutcome.Attach`, skip and failure caveats through
  `VerificationBudget.NotRunCaveats`: the verifying tools share them so they cannot drift.

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
- `.editorconfig` rules at `warning` are build errors (`EnforceCodeStyleInBuild`): file-scoped
  namespaces, `is null`, collection expressions, primary constructors. `dotnet format style dblens.slnx`
  fixes them. Raise a rule to `warning` only once the build is clean with it. FluentAssertions
  predicates are expression trees: keep `== null` there (`is null` is CS8122).
- Text a model reads (caveats, descriptions) never names a request default as something to raise:
  thresholds are not tool parameters. Say the limit, and what the caller can change.
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
- Every command comes from `Command(conn, sql)`, which sets `QueryTimeoutSeconds` (30).
  Don't catch `TimeoutException`: drivers raise `NpgsqlException` / `SqlException`.
- Generated SQL: `SqlIdentifier.Quote` / `Qualify` for every catalog name, `IndexSql` for index DDL.
  Never interpolate an identifier unquoted — quoting also keeps names inert to `StatementGuard`.
- Counts from a reader: `reader.Number("column")`, never `GetInt64` (SQL Server `COUNT` is `int`).
  Generated SQL Server probes use `COUNT_BIG`.
- Finding text is a locale-independent contract: `Num.N0` / `F1` / `F0` / `P0`, never `:N0`.
- Rule IDs and limits by constant: `*RuleIds`, `DiagnosticLimits.MaxTablesPerRequest` /
  `MaxFindingCount`. Never inline.
- After `ResolveTableScopeAsync` and `GetTableDetailsAsync`, append the scope's caveat and
  `NotFoundCaveat(details)` via `WithCaveats`; an empty scope returns `Empty()` with that caveat.
- A hook opens with `OpenAsync`, builds with `Command`, reads with `ReadAllAsync` and the `DbReading`
  extensions (`Text`, `Number`, `Flag`…), and runs caller or probe SQL through
  `InRolledBackTransactionAsync`. No `try`/`catch` for driver errors: `RunAsync` wraps them.
- A table is a `(schema, name)` pair from the catalog. Never join one into `"schema.name"` and split
  it again: names may contain dots. Only a name the caller typed goes through `ParseTableNames`.
- A catalog shape the demo schema lacks (partitions, odd names, index kinds) gets a table in the
  `dblens_edge` fixtures and a test in `EdgeCatalogTestsBase`, not a new demo table.
- Summary findings (`*-summary`) are exactly one, `Info`, never deduplicated.
- EF Core snapshots are never compiled or loaded: `SnapshotReader` reads syntax only. Test snapshots
  come from the real `dotnet ef` of each major (`scripts/generate-ef-fixtures.sh`); after changing
  `samples/DemoShop.EfCore`, regenerate them rather than editing them by hand.

## Defaults

```
PostgreSQL  Host=localhost;Port=5433;Database=dblens_demo;Username=dblens_reader;Password=readerpassword
SQL Server  Server=localhost,1434;Database=dblens_demo;User Id=dblens_reader;Password=ReaderPassword123!;TrustServerCertificate=true
```

In `appsettings.json`, with the same values as fallbacks in `Program.cs`; an empty value (an unset
`.mcpb` setting) means the default. The admin logins (`postgres` / `sa`) exist for setup only.
Non-standard ports avoid clashing with local installs.

## Releasing

Bump `<Version>` in `src/DbLens.Mcp/DbLens.Mcp.csproj`, both versions in
`src/DbLens.Mcp/.mcp/server.json`, the plugin's `version` and its `DbLens.Mcp@<version>` pin
(`ReleaseVersionTests` checks all five), merge, and release at once — the marketplace installs the
plugin from `master`, which pins a package NuGet lists only after the release: push a `v<version>`
tag, or run the Release workflow by hand with the version, which creates the tag. It builds binaries,
`.mcpb` extensions and the NuGet package, and publishes it via Trusted Publishing (OIDC, no stored
key). Never add a long-lived NuGet API key. See docs/architecture.md#releases.
