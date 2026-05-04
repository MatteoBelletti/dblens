# dblens

An MCP server written in .NET 10 that lets Claude inspect the schema of PostgreSQL and SQL Server databases. It runs entirely on your machine — no cloud, no data egress.

## What it does

dblens exposes six tools to any MCP-compatible client (Claude Desktop, Claude Code, MCP Inspector):

| Tool | What it returns |
|---|---|
| `ping` | Server version, timestamp, and available dialects — use to verify the connection |
| `get_schema` | High-level overview of all tables and views: row-count estimates, sizes, FK/index counts, grouped by schema |
| `get_table_details` | Full structural detail for 1–20 tables: columns with types and nullability, all indexes, foreign keys (in and out), check constraints, unique constraints |
| `analyze_indexes` | Diagnostic findings for index health: FK columns without a covering index, duplicate indexes, redundant indexes, and unused indexes (when stats are available) |
| `scan_gdpr` | Scans columns for PII/GDPR-relevant data by name and type patterns; identifies 5 PII categories and flags tables missing audit controls |
| `check_cascade_paths` | Traces ON DELETE CASCADE chains from a starting table; detects circular paths, deep chains, broken chains (SET NULL / RESTRICT), and self-referencing cascades |

The typical workflow is: `get_schema` to orient yourself, then `get_table_details` on the tables that matter, then `analyze_indexes` to surface index problems. Use `scan_gdpr` to identify GDPR exposure, and `check_cascade_paths` to understand or audit delete propagation before dropping rows.

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | 10.x | Runtime + build toolchain |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | any | Needed only for the bundled demo databases |
| [Claude Desktop](https://claude.ai/download) or Claude Code | any | MCP client |
| `make` | any | Windows: `winget install GnuWin32.Make` then reopen the terminal |
| Node.js / npx | any | Optional — only for `make dev` (MCP Inspector) |

## Quick start

```bash
# 1. Create the environment file (edit passwords if needed)
cp .env.example .env

# 2. Start the bundled demo databases
make db

# 3. Wait until both containers are healthy (~30-60 s on first run)
docker compose ps

# 4. Build, test, run
dotnet build
dotnet test
dotnet run --project src/DbLens.Mcp
```

The server starts and waits on stdin — that is the correct behaviour for MCP stdio transport. It exits when the client closes the pipe.

## Integrating with Claude Desktop

Add this block to your Claude Desktop config file, then restart Claude Desktop.

**Config file location:**
- Windows: `%APPDATA%\Claude\claude_desktop_config.json`
- macOS: `~/Library/Application Support/Claude/claude_desktop_config.json`

### Option A — published binary (recommended)

```bash
make publish   # → publish/DbLens.Mcp.exe
```

```json
{
  "mcpServers": {
    "dblens": {
      "command": "C:\\Users\\yourname\\dblens\\publish\\DbLens.Mcp.exe"
    }
  }
}
```

After each `make publish`, restart Claude Desktop to load the new binary.

### Option B — run from source (no re-publish needed)

```json
{
  "mcpServers": {
    "dblens": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\Users\\yourname\\dblens\\src\\DbLens.Mcp"]
    }
  }
}
```

Slower first startup due to JIT compilation; subsequent starts are fast.

## Integrating with Claude Code

```bash
claude mcp add dblens "C:\Users\yourname\dblens\publish\DbLens.Mcp.exe"
```

Or with `dotnet run`:

```bash
claude mcp add dblens dotnet -- run --project "C:\Users\yourname\dblens\src\DbLens.Mcp"
```

To reload after a code change: `/mcp` → dblens → **Restart**.

## Tool reference

### `ping`

No parameters. Returns server version, UTC timestamp, and the list of supported dialects. Use to confirm the server is reachable before doing anything else.

### `get_schema`

| Parameter | Type | Default | Description |
|---|---|---|---|
| `dialect` | `PostgreSql` \| `SqlServer` | `PostgreSql` | Target database engine |
| `connectionString` | string | server default | Custom connection string; omit to use the server's configured default |
| `schemaFilter` | string | — | Exact schema name, e.g. `public` |
| `namePattern` | string | — | SQL `LIKE` pattern applied to object names, e.g. `order%` |
| `includeViews` | bool | `true` | Whether to include views |
| `includeSystemSchemas` | bool | `false` | Whether to include system schemas (`pg_catalog`, `information_schema`, …) |

**Note:** `get_schema` returns a `ColumnCount` integer per object, not the column names. To get column names, types, and constraints, call `get_table_details`.

### `get_table_details`

| Parameter | Type | Default | Description |
|---|---|---|---|
| `tables` | string[] | required | 1–20 table names. Format: `schema.name` or just `name` (uses `defaultSchema`) |
| `dialect` | `PostgreSql` \| `SqlServer` | `PostgreSql` | Target database engine |
| `defaultSchema` | string | `public` / `dbo` | Schema used when a table name has no prefix |
| `connectionString` | string | server default | Custom connection string |

Tables not found in the database are listed in `NotFound`; they are absent from the result — do not infer their structure.

### `analyze_indexes`

| Parameter | Type | Default | Description |
|---|---|---|---|
| `dialect` | `PostgreSql` \| `SqlServer` | `PostgreSql` | Target database engine |
| `connectionString` | string | server default | Custom connection string |
| `schema` | string | — | Restrict analysis to this schema |
| `tables` | string[] | — | Restrict analysis to these table names (bare names, no schema prefix) |
| `includeUnusedIndexes` | bool | `true` | Whether to run the `unused-index` rule |
| `unusedIndexMinDays` | int | `7` | Minimum DB uptime (days) required before flagging unused indexes; prevents false positives after a restart |

**Rules:**

| Rule ID | Severity | What it detects |
|---|---|---|
| `fk-without-index` | High | A foreign-key column (or column set) with no covering index |
| `duplicate-index` | Medium | Two or more indexes with identical column lists and uniqueness setting |
| `redundant-index` | Low | A non-unique index whose columns are a strict prefix of another index |
| `unused-index` | Low | A non-unique index with zero scans since the last DB restart |

Each finding includes a `SqlFix` with a ready-to-run `CREATE INDEX` or `DROP INDEX` statement (dialect-specific syntax). Read `Caveats` before acting on `unused-index` recommendations.

### `scan_gdpr`

| Parameter | Type | Default | Description |
|---|---|---|---|
| `dialect` | `PostgreSql` \| `SqlServer` | `PostgreSql` | Target database engine |
| `connectionString` | string | server default | Custom connection string |
| `schema` | string | — | Restrict scan to this schema |
| `tables` | string[] | — | Restrict scan to these table names |
| `excludeTables` | string[] | — | Table names to exclude from the scan |
| `minConfidence` | double | `0.5` | Minimum match confidence to include (0.0–1.0) |

**Rules:**

| Rule ID | Severity | What it detects |
|---|---|---|
| `pii-identifier` | High (Medium with encryption marker) | Columns likely containing national IDs, passport numbers, SSN, fiscal codes |
| `pii-contact` | Medium | Email, phone, address columns |
| `pii-financial` | High | Credit card numbers, IBAN, bank account columns |
| `pii-special-category` | Critical | Health data, biometric data, religion, political views (Art. 9 GDPR) |
| `pii-location` | Low | Precise geolocation columns (latitude/longitude, GPS) |
| `missing-gdpr-controls` | Medium | PII-containing tables lacking soft-delete, created_at, or updated_at audit columns |

Results always include a `Disclaimer` field reminding you that column-name matching is heuristic — manual review is required. Read `Caveats` for any tables excluded from the scan.

### `check_cascade_paths`

| Parameter | Type | Default | Description |
|---|---|---|---|
| `dialect` | `PostgreSql` \| `SqlServer` | `PostgreSql` | Target database engine |
| `connectionString` | string | server default | Custom connection string |
| `startingTable` | string | — | Qualified starting table, e.g. `public.users` or `dbo.orders`. When omitted, analyzes all tables that are the target of at least one `ON DELETE CASCADE` FK |
| `schema` | string | — | Restrict analysis to this schema |
| `maxDepth` | int | `10` | Maximum cascade depth to explore (hard cap: 50) |

**Rules:**

| Rule ID | Severity | What it detects |
|---|---|---|
| `cascade-circular-path` | Critical | A cycle in the CASCADE chain (would cause runtime errors or mass deletions) |
| `cascade-deep-path` | High | Chain depth exceeds 5 levels |
| `cascade-breaks-with-restrict` | High | A `NO ACTION` / `RESTRICT` FK would block the DELETE at runtime (latent bug) |
| `cascade-breaks-with-set-null` | Info | Chain transitions to `SET NULL` — rows are retained, not deleted (relevant for GDPR right-to-erasure) |
| `cascade-self-referencing` | Info | Table has a self-referencing CASCADE FK (e.g. tree hierarchy) |
| `cascade-summary` | Info | Always present; summarizes total paths, max depth, and tables involved |

Results always include a `Disclaimer` field. Only schema-level FK constraints are analyzed — application-level deletions (ORM cascades, custom triggers) are not visible here.

## Connection strings

By default the server connects to the bundled demo databases:

```
PostgreSQL  Host=localhost;Port=5433;Database=dblens_demo;Username=postgres;Password=devpassword
SQL Server  Server=localhost,1434;Database=dblens_demo;User Id=sa;Password=DevPassword123!;TrustServerCertificate=true
```

To point at a different database, pass a `connectionString` parameter in the tool call. You can also change the defaults in `src/DbLens.Mcp/appsettings.json` or via environment variables.

## Demo databases

Both databases contain an intentionally flawed e-commerce schema (missing indexes, PII in plaintext, absent CHECK constraints) — useful for practising schema analysis with Claude. A `demo_good` schema with a clean design (all FK columns indexed, no duplicates or redundancies) is also included; `analyze_indexes` should return zero findings there.

**Manual connection:**

```bash
# PostgreSQL
psql -h localhost -p 5433 -U postgres -d dblens_demo

# SQL Server
sqlcmd -S localhost,1434 -U sa -P DevPassword123! -d dblens_demo -No -C
```

**Seed data (deterministic):**

| Table | Rows |
|---|---|
| categories | 4 |
| users | 20 |
| products | 30 |
| orders | 40 |
| order_items | 80 |
| item_reviews | 20 |
| payments | 35 |
| audit_logs | 100 |

## Project structure

```
dblens/
├── src/
│   ├── DbLens.Core/        Shared interfaces (IDatabaseInspector) and models
│   ├── DbLens.Postgres/    PostgreSQL implementation
│   ├── DbLens.SqlServer/   SQL Server implementation
│   └── DbLens.Mcp/         MCP server: entrypoint, tool handlers, DI wiring
├── tests/
│   └── DbLens.Tests/       xUnit tests (unit + integration)
├── db/
│   ├── postgres/           Schema + seed SQL for PostgreSQL
│   └── sqlserver/          Schema + seed SQL for SQL Server
├── scripts/
│   └── attach-debugger.ps1 Attaches Visual Studio debugger to a running process
└── publish/                Built binary (gitignored)
```

## Development

For the full development guide (workflows, debugging, MCP Inspector, Visual Studio attach) see [DEVELOPMENT.md](DEVELOPMENT.md).

Quick reference:

```bash
make dev           # MCP Inspector + stdio server with auto-rebuild (no debugger)
make dev-server    # SSE server on http://localhost:5050 with auto-rebuild
make inspector-sse # Open MCP Inspector pointed at the SSE server
make attach        # Attach Visual Studio debugger to running process
make test          # Run all tests
make test-watch    # Tests in watch mode
make build         # dotnet build
make publish       # Release binary → publish/DbLens.Mcp.exe
make db            # docker compose up -d
make db-down       # docker compose down
make db-logs       # Tail container logs
```

## Licence

MIT
