# dblens

An MCP server written in .NET that connects to relational databases (PostgreSQL and SQL Server) and exposes tools to Claude Desktop for schema analysis. Runs fully locally — zero cloud, zero data egress.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/)
- [Claude Desktop](https://claude.ai/download)

## Quick start

```bash
# 1. Copy environment file and adjust passwords if needed
cp .env.example .env

# 2. Start databases (first run will initialise schema + seed data)
docker compose up -d

# 3. Wait until both healthchecks pass (~30-60 s on first run)
docker compose ps

# 4. Build, test, run
dotnet build
dotnet test
dotnet run --project src/DbLens.Mcp
```

The MCP server starts and waits on stdin — this is correct behaviour. It exits when the client closes the pipe.

## Configuring Claude Desktop

Add the following block to your Claude Desktop config file and restart Claude Desktop.

**Config file location:**
- **Windows:** `%APPDATA%\Claude\claude_desktop_config.json`
- **macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`

```json
{
  "mcpServers": {
    "dblens": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "<ABSOLUTE_PATH_TO_DBLENS>\\src\\DbLens.Mcp"
      ]
    }
  }
}
```

> **Replace `<ABSOLUTE_PATH_TO_DBLENS>`** with the full path to this repository root.
> On Windows example: `C:\Users\yourname\dblens`

For a faster startup after first build, point directly to the compiled binary:

```json
{
  "mcpServers": {
    "dblens": {
      "command": "<ABSOLUTE_PATH_TO_DBLENS>\\src\\DbLens.Mcp\\bin\\Debug\\net10.0\\DbLens.Mcp.exe"
    }
  }
}
```

After saving and restarting Claude Desktop, ask: *"fai ping al server dblens"* — you should see the `ping` tool invocation and a structured response.

## Project structure

```
dblens/
├── db/
│   ├── postgres/          SQL schema + seed for PostgreSQL
│   └── sqlserver/         SQL schema + seed + init script for SQL Server
├── src/
│   ├── DbLens.Core/       Shared abstractions (IDatabaseInspector, models)
│   ├── DbLens.Postgres/   PostgreSQL implementation (skeleton — Phase 2)
│   ├── DbLens.SqlServer/  SQL Server implementation (skeleton — Phase 2)
│   └── DbLens.Mcp/        Executable MCP server + tools
└── tests/
    └── DbLens.Tests/      xUnit tests
```

## Test databases

Both databases contain an e-commerce schema with deliberate issues (missing indexes, PII in plaintext, missing CHECK constraints) — they are the target of future analysis tools.

**Connect manually:**

```bash
# PostgreSQL
psql -h localhost -p 5433 -U postgres -d dblens_demo

# SQL Server (requires sqlcmd)
sqlcmd -S localhost,1434 -U sa -P DevPassword123! -d dblens_demo -No -C
```

**Row counts (deterministic seed):**

| Table       | Rows |
|-------------|------|
| categories  | 4    |
| users       | 20   |
| products    | 30   |
| orders      | 40   |
| order_items | 80   |
| payments    | 35   |
| audit_logs  | 100  |

## Current status

Scaffolding phase complete. The single implemented tool is `ping`, which verifies server reachability and returns basic server info. Real analysis tools are coming in Phase 2.

## Roadmap

1. `get_schema` — list tables, columns, types, and constraints for a database
2. `analyze_indexes` — detect missing indexes and unused indexes
3. `scan_gdpr` — identify PII columns and GDPR risk signals
4. `analyze_query` — parse and explain a SQL query, suggest optimisations
5. `get_table_stats` — row counts, bloat estimates, last vacuum/analyze

> **Note:** Never commit `.env` — it is already in `.gitignore`.
