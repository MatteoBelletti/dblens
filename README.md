# dblens

An MCP server, written in .NET 10, that lets Claude (or any MCP client) understand and diagnose
PostgreSQL and SQL Server databases. It runs locally, never writes to the database, and by default
never returns row data. Published on NuGet as **`DbLens.Mcp`**.

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

## Tools

| Tool | Answers |
|---|---|
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
| `check_migration_safety` | Will this migration lock, rewrite or destroy anything, given real table sizes? |
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
| `ConnectionStrings:PostgreSql` | `ConnectionStrings__PostgreSql` | the demo database, as `dblens_reader` |
| `ConnectionStrings:SqlServer` | `ConnectionStrings__SqlServer` | the demo database, as `dblens_reader` |
| `DbLens:AllowQueryResults` | `DbLens__AllowQueryResults` | `false` — enables `query_with_receipt` |
| `DbLens:AllowWriteCapableLogin` | `DbLens__AllowWriteCapableLogin` | `false` — skips the read-only-login check on remote servers too; disposable databases only |
| `DbLens:ReadOnlyLoginOnLocalhost` | `DbLens__ReadOnlyLoginOnLocalhost` | `false` — applies the check to a local server as well, for dblens installed on a real database server |
| `DbLens:CertifiedQueriesPath` | `DbLens__CertifiedQueriesPath` | `dblens/certified-queries.json` under the local application-data folder: `%LOCALAPPDATA%` on Windows, `~/.local/share` on Linux, `~/Library/Application Support` on macOS |

An empty value means the default, so an unset Claude Desktop setting is harmless. A tool call can
also pass its own `connectionString`. `analyze_fragmentation` additionally needs
`pgstattuple` (PostgreSQL) or `VIEW DATABASE STATE` (SQL Server); query history needs
`pg_stat_statements` or Query Store.

Demo logins:

```
PostgreSQL  Host=localhost;Port=5433;Database=dblens_demo;Username=dblens_reader;Password=readerpassword
SQL Server  Server=localhost,1434;Database=dblens_demo;User Id=dblens_reader;Password=ReaderPassword123!;TrustServerCertificate=true
```

## Demo databases

`public` / `dbo` is an e-commerce schema with deliberate flaws — unindexed foreign keys, duplicate
indexes, plaintext PII, cascade chains, cached counts that have drifted, undeclared references (one
visible only through a view). `demo_good` is a clean design on which every tool reports nothing.
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
