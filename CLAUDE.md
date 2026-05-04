# CLAUDE.md — dblens

## What this project is

dblens is an MCP server written in .NET 10. It connects to PostgreSQL and SQL Server databases and exposes schema inspection tools to Claude (or any MCP-compatible client). Everything runs locally — there is no cloud component.

## Build and test

```bash
dotnet build
dotnet test
```

Tests require both Docker databases to be running (`make db`). Integration tests hit real databases — there are no mocked database tests.

```bash
make db          # docker compose up -d (PostgreSQL 5433, SQL Server 1434)
make test        # run all xUnit tests
make test-watch  # watch mode
```

## Running the server

```bash
# stdio (default) — how Claude Desktop/Code launches it
dotnet run --project src/DbLens.Mcp

# SSE (for debugging with MCP Inspector)
MCP_TRANSPORT=sse dotnet run --project src/DbLens.Mcp
# → http://localhost:5050/mcp
```

Or use the Makefile:
```bash
make dev         # MCP Inspector + stdio, auto-rebuild on save
make dev-server  # SSE on port 5050, auto-rebuild
```

## Architecture

```
DbLens.Core         → interfaces (IDatabaseInspector) and all shared models
DbLens.Postgres     → PostgresInspector : IDatabaseInspector
DbLens.SqlServer    → SqlServerInspector : IDatabaseInspector
DbLens.Mcp          → MCP server: Program.cs, tool classes, DI wiring
DbLens.Tests        → xUnit tests
```

**Data flow:** MCP client → `DbLens.Mcp` tool → `IDatabaseInspectorFactory` → dialect-specific `IDatabaseInspector` → database.

Inspectors are registered as **keyed singletons** using `DatabaseDialect` as the key. When a tool call includes a custom `connectionString`, `IDatabaseInspectorFactory.Create(dialect, connectionString)` constructs an ad-hoc inspector instead.

## Adding a new MCP tool

1. Create a class in `src/DbLens.Mcp/Tools/` decorated with `[McpServerToolType]`.
2. Add a public method decorated with `[McpServerTool]` and a `[Description(...)]`.
3. Inject `IServiceProvider` and/or `IDatabaseInspectorFactory` via the constructor — no other registration needed.
4. Tools are discovered automatically via `WithToolsFromAssembly(typeof(PingTool).Assembly)`.

Example skeleton:

```csharp
[McpServerToolType]
public sealed class MyTool(IServiceProvider services, IDatabaseInspectorFactory factory)
{
    private IKeyedServiceProvider KeyedServices => (IKeyedServiceProvider)services;

    [McpServerTool, Description("...")]
    public async Task<MyResult> MyMethod(
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        var inspector = connectionString is not null
            ? factory.Create(dialect, connectionString)
            : KeyedServices.GetRequiredKeyedService<IDatabaseInspector>(dialect);

        return await inspector.SomeMethodAsync(cancellationToken);
    }
}
```

## Adding a new inspector capability

1. Add the method signature to `IDatabaseInspector` in `DbLens.Core`.
2. Add request/response models to `DbLens.Core/Models/` (or `DbLens.Core/Analysis/<FeatureName>/`).
3. Implement the method in both `PostgresInspector` and `SqlServerInspector` as a **partial class** in a separate file (e.g. `PostgresInspector.Analyze.cs`).
4. Write integration tests in `DbLens.Tests` for both dialects; add a parity test if relevant.

## Diagnostic tools pattern

Diagnostic tools (e.g. `analyze_indexes`) follow this layout:

```
DbLens.Core/Analysis/
  Severity.cs             enum Severity { Critical, High, Medium, Low, Info }
  FindingCategory.cs      enum FindingCategory { Performance, Compliance, Structure, Naming }
  Finding.cs              sealed record — permanent product contract (do not change shape)
  <FeatureName>/
    <Feature>Request.cs
    <Feature>Result.cs
    Rules/
      <RuleName>Rule.cs   public static class with Apply(tables, dialect) → IEnumerable<Finding>
```

### `Finding` contract (immutable — do not change without an explicit decision)

```csharp
public sealed record Finding(
    string RuleId,
    Severity Severity,
    FindingCategory Category,
    double Confidence,       // 0.0–1.0
    string Table,            // "schema.table"
    IReadOnlyList<string>? Columns,
    string Title,
    string Description,
    string? SqlFix,
    IReadOnlyDictionary<string, object>? Metadata);
```

### Result summary pattern

Every diagnostic result must include a `Summary` with `BySeverity` and `ByRule` dictionaries that **always contain all keys**, even when the count is zero. This allows callers to read any key without a null check.

### `demo_good` schema

Both databases include a `demo_good` schema with a clean design (FKs fully covered, no duplicates, no CASCADE chains). The acceptance criterion for any diagnostic rule is: running it scoped to `demo_good` → zero diagnostic findings (only the always-present summary finding is allowed).

## LLM guardrails in tool descriptions

Both `get_schema` and `get_table_details` contain explicit `IMPORTANT:` lines in their `[Description]` attributes instructing Claude not to infer, guess, or hallucinate data that is not present in the response (e.g. column names from `get_schema`, view SQL bodies, row data). When adding new tools, follow this pattern: state explicitly what is **not** included in the response.

## Transport modes

| `MCP_TRANSPORT` | Port | When to use |
|---|---|---|
| `stdio` (default) | — | Claude Desktop, Claude Code, production |
| `sse` | 5050 | Debugging with Visual Studio + MCP Inspector |

The launch profile `sse` in `launchSettings.json` sets `MCP_TRANSPORT=sse` automatically.

## Logging

All logging goes to **stderr**. Stdout is reserved exclusively for the MCP protocol. This is configured in `ConfigureLogging` in `Program.cs` via `LogToStandardErrorThreshold = LogLevel.Trace`.

## Connection string defaults

Defaults are in `appsettings.json` and also hardcoded as fallbacks in `Program.cs`:

```
PostgreSQL  Host=localhost;Port=5433;Database=dblens_demo;Username=postgres;Password=devpassword
SQL Server  Server=localhost,1434;Database=dblens_demo;User Id=sa;Password=DevPassword123!;TrustServerCertificate=true
```

The demo databases run on non-standard ports (5433, 1434) to avoid colliding with any locally installed instances.

## Key files

| File | Purpose |
|---|---|
| `src/DbLens.Mcp/Program.cs` | Entry point: transport selection, DI wiring |
| `src/DbLens.Core/Abstractions/IDatabaseInspector.cs` | Inspector contract |
| `src/DbLens.Core/Analysis/Finding.cs` | Permanent product contract for all diagnostic findings |
| `src/DbLens.Core/Analysis/DiagnosticLimits.cs` | Shared numeric limits (MaxTablesPerRequest, MaxFindingCount) |
| `src/DbLens.Core/Analysis/RuleIds.cs` | Rule ID string constants (`IndexRuleIds`, `GdprRuleIds`, `CascadeRuleIds`) with `All` arrays |
| `src/DbLens.Core/Analysis/Indexes/` | Request/result types and rules for `analyze_indexes` |
| `src/DbLens.Core/Analysis/Gdpr/` | Request/result types, pattern dictionaries, classifier, and engine for `scan_gdpr` |
| `src/DbLens.Core/Analysis/Gdpr/Patterns/` | Embedded JSON pattern files (`pii_patterns_en.json`, `pii_patterns_it.json`) |
| `src/DbLens.Core/Analysis/Cascade/` | Request/result types and `CascadeAnalysisEngine` for `check_cascade_paths` |
| `src/DbLens.Postgres/PostgresInspector.cs` | PostgreSQL base implementation |
| `src/DbLens.Postgres/PostgresInspector.Analyze.cs` | PostgreSQL `AnalyzeIndexesAsync` (partial class) |
| `src/DbLens.Postgres/PostgresInspector.ScanGdpr.cs` | PostgreSQL `ScanGdprAsync` (partial class) |
| `src/DbLens.Postgres/PostgresInspector.CheckCascade.cs` | PostgreSQL `CheckCascadePathsAsync` (partial class) |
| `src/DbLens.SqlServer/SqlServerInspector.cs` | SQL Server base implementation |
| `src/DbLens.SqlServer/SqlServerInspector.Analyze.cs` | SQL Server `AnalyzeIndexesAsync` (partial class) |
| `src/DbLens.SqlServer/SqlServerInspector.ScanGdpr.cs` | SQL Server `ScanGdprAsync` (partial class) |
| `src/DbLens.SqlServer/SqlServerInspector.CheckCascade.cs` | SQL Server `CheckCascadePathsAsync` (partial class) |
| `src/DbLens.Mcp/Infrastructure/DatabaseInspectorFactory.cs` | Creates ad-hoc inspectors for custom connection strings |
| `src/DbLens.Mcp/Tools/` | All MCP tool classes |
| `docker-compose.yml` | Demo databases (PostgreSQL 17 on 5433, SQL Server 2022 on 1434) |
| `db/postgres/01-schema.sql` | PostgreSQL demo schema (`public` + `demo_good`) |
| `db/sqlserver/01-schema.sql` | SQL Server demo schema (`dbo` + `demo_good`) |
| `Makefile` | All dev commands |
| `Directory.Packages.props` | Central NuGet version management |
| `Directory.Build.props` | Shared build settings (net10.0, C# 14, nullable, warnings as errors) |

## Conventions

- Target framework: `net10.0`, C# 14, nullable reference types enabled, warnings as errors.
- No comments unless the reason is non-obvious.
- Integration tests must hit real databases — no mocking the data layer.
- Both PostgreSQL and SQL Server must be kept at feature parity for any new inspector capability.
- Add a parity test file (`<Feature>ParityTests.cs`) for every new diagnostic tool that compares findings across dialects (strip schema prefix with `f.Table.Split('.')[1]` for bare-table comparisons).
- Stub inspectors in `GetSchemaToolTests` and `GetTableDetailsToolTests` must implement all `IDatabaseInspector` methods; add `throw new NotSupportedException()` for new methods.
- Use `DiagnosticLimits` constants (never magic numbers) for `MaxTablesPerRequest` and `MaxFindingCount`. Note: `[Description]` attribute values must be compile-time constants — use literals there, not `nameof` or interpolation.
- Use rule ID constants from `IndexRuleIds` / `GdprRuleIds` everywhere (rules, summary builders, tests). Never write rule ID strings inline.
- Command timeouts: all database commands must set `CommandTimeout = QueryTimeoutSeconds` (constant = 30). Npgsql's default is 0 (infinite) unless set in the connection string. SqlClient's default is 30s but set it explicitly for clarity.
- Do not catch `System.TimeoutException` from database drivers — Npgsql raises `NpgsqlException` and SqlClient raises `SqlException` on command timeout; `TimeoutException` catches are dead code.
- Test fixture connection strings: use `Environment.GetEnvironmentVariable("DBLENS_TEST_PG_CONNECTION")` / `"DBLENS_TEST_SS_CONNECTION"` with a hardcoded default fallback. Never hardcode credentials as the sole source of truth.
- `NotFound` propagation: after calling `GetTableDetailsAsync` from a diagnostic method, always check `details.NotFound` and append a caveat to the result using `result = result with { Caveats = [..result.Caveats, extra] }`.
- Always-present summary finding: some tools (e.g. `check_cascade_paths`) include an unconditional `*-summary` finding appended after deduplication. This finding is always exactly one, always `Severity.Info`, and must never be deduplicated away.
- `CascadeAnalysisEngine` pattern: when analysis logic is purely in-memory (no DB access), extract it into a static engine class in `DbLens.Core/Analysis/<Feature>/`. The dialect-specific inspector only handles DB queries and passes raw data to the engine. This makes the engine fully unit-testable without a database.
