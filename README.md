# dblens

An MCP server, written in .NET 10, that lets Claude (or any MCP client) understand and diagnose
PostgreSQL and SQL Server databases. It runs locally, never writes to the database and never returns
row data.

## Tools

| Tool | Answers |
|---|---|
| `ping` | Is the server up? |
| `get_schema` | What tables and views are there, how big? |
| `get_table_details` | Columns, indexes, keys and constraints of these tables |
| `infer_relationships` | How do tables join — including foreign keys nobody declared? |
| `analyze_indexes` | Which FKs lack an index, which indexes are duplicate, redundant or unused? |
| `analyze_fragmentation` | Which indexes are fragmented or sparsely packed? |
| `analyze_denormalization` | Where is data repeated, cached or packed into one column — and has it drifted? |
| `scan_gdpr` | Which columns look like personal data? |
| `check_cascade_paths` | What does a DELETE on this table actually remove? |
| `check_migration_safety` | Will this migration lock, rewrite or destroy anything, given real table sizes? |
| `explain_query` | Why is this query slow? |

Full parameters, rules and limits: **[docs/tools.md](docs/tools.md)**.

A typical session: `get_schema` to orient, `get_table_details` and `infer_relationships` to learn the
structure, then the diagnostic tool that fits the question. For a slow query start from
`explain_query`; before shipping DDL run it through `check_migration_safety`.

## Facts and guesses are kept apart

Every diagnostic tool returns two lists:

- **`Findings`** — what dblens established: measured (fragmentation, scan counters, counted rows) or
  derived from the catalog (a cascade cycle, a duplicate index, the lock a DDL takes).
- **`Signals`** — what it suspects from names and structure. `fiscal_code` probably holds a fiscal
  code. Or nothing. Or something else.

They are separate lists, not a flag, because a flag disappears the moment a response is summarized.
`scan_gdpr` reads names only, so its `Findings` list is always empty.

Two tools can test their own guesses. With `verifySignals` / `verifyCandidates`, dblens runs one
generated, read-only, rolled-back aggregate query per checkable signal and moves the measured ones
into `Findings`:

> `customer_profiles.order_count` looks like a cached aggregate *(signal, confidence 0.6)*

becomes

> `public.customer_profiles`: **3 of 10 rows** disagree with a live count of child rows *(finding, measured)*

and

> `shipments.order_id` looks like an undeclared reference to `orders.id` *(signal)*

becomes

> all 6 non-null values exist in `public.orders` *(finding, measured, with the `ALTER TABLE` to declare it)*

Probes return counts only — how many rows, never which ones. What runs, and what stops it from doing
harm: **[docs/safety.md](docs/safety.md)**.

## Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and, for the demo
databases, Docker.

```bash
cp .env.example .env
docker compose up -d       # PostgreSQL on 5433, SQL Server on 1434 (~1 min the first time)
dotnet build
dotnet test
dotnet run --project src/DbLens.Mcp
```

The server then waits on stdin: that is the MCP stdio transport working.

## Connect a client

Build a binary with `make publish` (→ `publish/DbLens.Mcp.exe`), then:

**Claude Code**

```bash
claude mcp add dblens "C:\path\to\dblens\publish\DbLens.Mcp.exe"
```

**Claude Desktop** — `%APPDATA%\Claude\claude_desktop_config.json` on Windows,
`~/Library/Application Support/Claude/claude_desktop_config.json` on macOS:

```json
{
  "mcpServers": {
    "dblens": { "command": "C:\\path\\to\\dblens\\publish\\DbLens.Mcp.exe" }
  }
}
```

To run from source instead, use `"command": "dotnet"` with
`"args": ["run", "--project", "C:\\path\\to\\dblens\\src\\DbLens.Mcp"]`. Restart the client after
each publish.

## Connection strings

By default dblens connects to the demo databases:

```
PostgreSQL  Host=localhost;Port=5433;Database=dblens_demo;Username=postgres;Password=devpassword
SQL Server  Server=localhost,1434;Database=dblens_demo;User Id=sa;Password=DevPassword123!;TrustServerCertificate=true
```

Change them in `src/DbLens.Mcp/appsettings.json` or through environment variables, or pass
`connectionString` on a single tool call. Use an account with read access to the catalog;
`analyze_fragmentation` additionally needs `pgstattuple` (PostgreSQL) or `VIEW DATABASE STATE`
(SQL Server).

## Demo databases

`public` / `dbo` is an e-commerce schema with deliberate flaws — unindexed foreign keys, duplicate
indexes, plaintext PII, cascade chains, cached counts that have drifted, an undeclared reference.
`demo_good` is a clean design on which every tool reports nothing. Details in
[docs/architecture.md](docs/architecture.md#demo-databases).

## Documentation

| Document | For |
|---|---|
| [docs/tools.md](docs/tools.md) | Every tool: parameters, rules, severities, limits |
| [docs/safety.md](docs/safety.md) | What can execute, and the guarantees around it |
| [docs/architecture.md](docs/architecture.md) | How the code is organized, the verification pipeline, testing |
| [GLOSSARY.md](GLOSSARY.md) | Finding, signal, evidence, probe… |
| [DEVELOPMENT.md](DEVELOPMENT.md) | Dev workflows, MCP Inspector, debugging (Italian) |
| [CLAUDE.md](CLAUDE.md) | Conventions for contributors and coding agents |

## Licence

MIT
