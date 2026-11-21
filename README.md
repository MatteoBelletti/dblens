# dblens

**Before an EF Core migration reaches production, dblens checks it against a copy of the database:
what it locks or rewrites at the tables' real size, what it destroys, and what production holds that
your model does not know about.**

```
compare_ef_model         → 2 migrations pending, 1 applied by hand that the code lacks,
                           users.phone missing, an index nobody modeled
dotnet ef migrations script …   (the command it gives you)
check_migration_safety   → CREATE INDEX on a 12 M-row table without CONCURRENTLY: blocks writes
```

It is an MCP server for Claude or any MCP client, on PostgreSQL and SQL Server, written in .NET 10.
It runs locally, never writes to the database and, by default, returns no row data. Published on
NuGet as **`DbLens.Mcp`**.

## Install

The package is [`DbLens.Mcp`](https://www.nuget.org/packages/DbLens.Mcp) on NuGet — not `dblens`,
which is an unrelated tool. Every release also ships on
[GitHub Releases](https://github.com/MatteoBelletti/dblens/releases).

**Claude Desktop** — download `dblens-<version>-<platform>.mcpb` from
[Releases](https://github.com/MatteoBelletti/dblens/releases), open it, and fill in the connection
strings when asked.

**Any MCP client, with the .NET 10 runtime** — install the `DbLens.Mcp` tool and point the client at it:

```bash
dotnet tool install -g DbLens.Mcp
```

```bash
claude mcp add dblens -e "ConnectionStrings__PostgreSql=Host=…;Username=dblens_reader;Password=…" -- dblens
```

**Without .NET** — the release also carries self-contained binaries (`dblens-<version>-<rid>.zip`);
use the extracted `DbLens.Mcp` executable as the command.

**From source** — see [Quick start](#quick-start).

**Use a read-only login for any shared database.** dblens refuses to execute caller SQL on a login
that can write when the server is on another machine, so create one first — two statements, in
[docs/safety.md](https://github.com/MatteoBelletti/dblens/blob/master/docs/safety.md#creating-the-login). A database on your own machine (localhost,
loopback, socket, `(local)`, LocalDB) is exempt: the admin login works there.

## Claude Code plugin (optional)

The server works on its own, in any client. In Claude Code the `dblens` plugin adds a repeatable audit:
agents run the diagnostics in parallel, a verifier re-runs every finding, and a report keeps the
evidence ([decisions](docs/roadmap/decisions.md)). It needs the .NET 10 SDK (the plugin starts the
server with `dnx`) and dynamic workflows enabled in `/config`.

```
/plugin install dblens --marketplace MatteoBelletti/dblens
```

Then `/dblens:setup` checks the connection and runs a first audit; `/dblens:audit` runs one with
`dialect`, `outDir` and `runLabel`, and optionally `schema`, `efSnapshot` (an EF Core migrations
snapshot), `efScript` (lets it script the pending migrations with `dotnet ef`, which builds the
project), `queriesFile` (SQL to plan) and `privacy`. Set the connection strings in the
plugin's settings, never in the chat. Keep one registration: if `dblens` was also added with
`claude mcp add`, remove one of the two.

### Reviewing a .NET branch (optional)

The same marketplace has `dotnet-review`, which needs `dblens`: `/dotnet-review:review` with `base`,
`branch` and `outDir` sorts the changed files, has one agent review each touched area (EF
migrations, EF queries, the public API contract), drops every comment its evidence does not
support, and writes at most five to `review-<branch>.md`. It posts nothing.

```
/plugin install dotnet-review --marketplace MatteoBelletti/dblens
```

## Checking a migration

1. Restore a recent dump locally and point dblens at it (or use a read-only login on a copy).
2. **`compare_ef_model`** with the path of your `Migrations` folder: pending migrations, migrations
   applied in the database that the code does not have, and tables, columns, indexes and foreign keys
   on one side only, each with the entity and the fluent call to change. It gives the
   `dotnet ef migrations script` command for what is pending.
3. **`check_migration_safety`** on that script: table rewrites, blocking locks, index builds without
   `CONCURRENTLY` / `ONLINE`, constraints validated against every row, and destructive statements —
   weighted by the real size of each table. Use the non-idempotent script; the history-table insert
   is listed as a statement it does not recognize.

Neither step executes the migration: the snapshot is read as text and the script is never run.

## Tools

The two above, and the rest of what dblens can tell about a database:

| Tool | Answers |
|---|---|
| `compare_ef_model` | What does production have that the EF Core model does not know — and which migrations are pending? |
| `check_migration_safety` | Will this migration lock, rewrite or destroy anything, given real table sizes? |
| `ping` | Is the server up? |
| `get_schema` | What tables and views are there, how big? |
| `get_table_details` | Columns, indexes, keys and constraints of these tables |
| `infer_relationships` | How do tables join — including foreign keys nobody declared, found in views, routines and query history? |
| `build_glossary` | Write me a data map of this database as a skill |
| `analyze_indexes` | Which FKs lack an index, which indexes are duplicate, redundant or unused? |
| `analyze_fragmentation` | Which indexes are fragmented or sparsely packed? |
| `analyze_denormalization` | Where is data repeated, cached or packed into one column — and has it drifted? |
| `scan_gdpr` | Which columns look like personal data? |
| `check_cascade_paths` | What does a DELETE on this table actually remove? |
| `analyze_table_hygiene` | Which columns does nobody fill, and which tables lack or neglect their creation and update dates? |
| `find_hot_queries` | Which queries weigh most — and which joins multiply rows? Found from the engine's own statistics |
| `explain_query` | Why is this query slow? |
| `query_with_receipt` | What is the figure, and what is the proof? *(off unless the operator enables it)* |
| `certify_query`, `list_certified_queries` | Remember the queries the user confirmed as right |

Full parameters, rules and limits: **[docs/tools.md](https://github.com/MatteoBelletti/dblens/blob/master/docs/tools.md)**.

A typical session: `get_schema` to orient, `infer_relationships` and `build_glossary` to learn the
structure, then the diagnostic tool that fits the question. For a slow query start from
`explain_query`; before shipping DDL run it through `check_migration_safety`.

## Facts and guesses are kept apart

Every diagnostic tool returns two lists:

- **`Findings`** — what dblens established: measured (fragmentation, scan counters, counted rows) or
  derived from the catalog (a cascade cycle, a duplicate index, the lock a DDL takes).
- **`Signals`** — what it suspects from names, structure or the SQL the database runs. `fiscal_code`
  probably holds a fiscal code. Or nothing. Or something else.

They are separate lists, not a flag, because a flag disappears the moment a response is summarized.

Three tools can test their own guesses: `analyze_denormalization` (`verifySignals`),
`infer_relationships` (`verifyCandidates`) and `build_glossary` (`verifyJoins`). dblens then runs one
generated, read-only, rolled-back aggregate query per checkable signal and moves the measured ones into
`Findings`:

> `shipments.ship_to` is joined to `addresses.id` by the view `shipment_destinations` *(signal)*

becomes

> all 6 non-null values exist in `public.addresses` *(finding, measured, with the `ALTER TABLE` to declare it)*

Probes return counts only — how many rows, never which ones — and run within a fixed budget (20
queries, 10 s each, 30 s per call, tables up to 5 million rows). What the budget leaves out is named
in a caveat, not silently dropped.

## Answers with a receipt

When the operator enables it (`DbLens:AllowQueryResults=true`), `query_with_receipt` answers with the
figure *and* the evidence: the exact SQL, the tables the plan read, and — given a second query that
computes the same figure another way — whether the two agree. If they disagree, dblens says so instead
of choosing. Queries the user confirms can be certified and re-run by name; the glossary lists them.

It is off by default because it is the one tool that returns values from the data. What runs, and
what stops it from doing harm: **[docs/safety.md](https://github.com/MatteoBelletti/dblens/blob/master/docs/safety.md)**.

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

The server then waits on stdin: that is the MCP stdio transport working. To use a build from source
in Claude Code:

```bash
claude mcp add dblens -- dotnet run --project /path/to/dblens/src/DbLens.Mcp
```

## Configuration

| Key (`appsettings.json`) | Environment variable | Default |
|---|---|---|
| `ConnectionStrings:PostgreSql` | `ConnectionStrings__PostgreSql` | none: a call without `connectionString` is an error |
| `ConnectionStrings:SqlServer` | `ConnectionStrings__SqlServer` | none: a call without `connectionString` is an error |
| `DbLens:DemoFallback` | `DbLens__DemoFallback` | `false` — `true` uses the demo database for a dialect with no connection string; `dotnet run` and `make` turn it on |
| `DbLens:AllowQueryResults` | `DbLens__AllowQueryResults` | `false` — enables `query_with_receipt` |
| `DbLens:AllowWriteCapableLogin` | `DbLens__AllowWriteCapableLogin` | `false` — skips the read-only-login check on remote servers too; disposable databases only |
| `DbLens:ReadOnlyLoginOnLocalhost` | `DbLens__ReadOnlyLoginOnLocalhost` | `false` — applies the check to a local server as well, for dblens installed on a real database server |
| `DbLens:ConnectionsPath` | `DbLens__ConnectionsPath` | `dblens/connections` under the local application-data folder — connections saved under a name, one `<name>.conn` each (dialect, then connection string), for the `connection` parameter |
| `DbLens:CertifiedQueriesPath` | `DbLens__CertifiedQueriesPath` | `dblens/certified-queries.json` under the local application-data folder: `%LOCALAPPDATA%` on Windows, `~/.local/share` on Linux, `~/Library/Application Support` on macOS |

An empty value means the default, so an unset Claude Desktop setting counts as not configured. A
tool call can also pass its own `connectionString`. `analyze_fragmentation` additionally needs
`pgstattuple` (PostgreSQL) or `VIEW DATABASE STATE` (SQL Server); query history needs
`pg_stat_statements` or Query Store.

Demo logins:

```
PostgreSQL  Host=localhost;Port=5433;Database=dblens_demo;Username=dblens_reader;Password=readerpassword
SQL Server  Server=localhost,1434;Database=dblens_demo;User Id=dblens_reader;Password=ReaderPassword123!;TrustServerCertificate=true
```

## Demo databases

`public` / `dbo` is an e-commerce schema with deliberate flaws — unindexed foreign keys,
plaintext PII, cascade chains, cached counts that have drifted, undeclared references (one
visible only through a view). `demo_good` is a clean design on which every tool reports nothing.
`shop` is the EF Core model of `samples/DemoShop.EfCore`, drifted, with two migrations pending.
Details in [docs/architecture.md](https://github.com/MatteoBelletti/dblens/blob/master/docs/architecture.md#demo-databases).

## Documentation

| Document | For |
|---|---|
| [docs/tools.md](https://github.com/MatteoBelletti/dblens/blob/master/docs/tools.md) | Every tool: parameters, rules, severities, limits |
| [docs/safety.md](https://github.com/MatteoBelletti/dblens/blob/master/docs/safety.md) | The contract, what can execute, the read-only login |
| [docs/architecture.md](https://github.com/MatteoBelletti/dblens/blob/master/docs/architecture.md) | How the code is organized, pipelines, testing, releases |
| [GLOSSARY.md](https://github.com/MatteoBelletti/dblens/blob/master/GLOSSARY.md) | Finding, signal, evidence, probe, receipt… |
| [DEVELOPMENT.md](https://github.com/MatteoBelletti/dblens/blob/master/DEVELOPMENT.md) | Dev workflows, MCP Inspector, debugging (Italian) |
| [CLAUDE.md](https://github.com/MatteoBelletti/dblens/blob/master/CLAUDE.md) | Conventions for contributors and coding agents |

## Licence

[MIT](https://github.com/MatteoBelletti/dblens/blob/master/LICENSE)
