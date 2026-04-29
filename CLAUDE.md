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
2. Add request/response models to `DbLens.Core/Models/`.
3. Implement the method in both `PostgresInspector` and `SqlServerInspector`.
4. Write integration tests in `DbLens.Tests` for both dialects; add a parity test in `TableDetailsParityTests` if relevant.

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
| `src/DbLens.Postgres/PostgresInspector.cs` | PostgreSQL implementation |
| `src/DbLens.SqlServer/SqlServerInspector.cs` | SQL Server implementation |
| `src/DbLens.Mcp/Infrastructure/DatabaseInspectorFactory.cs` | Creates ad-hoc inspectors for custom connection strings |
| `src/DbLens.Mcp/Tools/` | All MCP tool classes |
| `docker-compose.yml` | Demo databases (PostgreSQL 17 on 5433, SQL Server 2022 on 1434) |
| `Makefile` | All dev commands |
| `Directory.Packages.props` | Central NuGet version management |
| `Directory.Build.props` | Shared build settings (net10.0, C# 14, nullable, warnings as errors) |

## Conventions

- Target framework: `net10.0`, C# 14, nullable reference types enabled, warnings as errors.
- No comments unless the reason is non-obvious.
- Integration tests must hit real databases — no mocking the data layer.
- Both PostgreSQL and SQL Server must be kept at feature parity for any new inspector capability.
