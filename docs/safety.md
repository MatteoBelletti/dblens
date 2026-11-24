# Safety model

dblens is pointed at real databases by a language model. This page states the contract, everything
that can execute, and what stops each of those things from doing harm.

## The contract

- **No writes.** No tool changes the database. Proposed DDL is returned as text (`SqlFix.Kind = Ddl`)
  for a human to review.
- **No row data — by default.** Every tool returns metadata, plans and counts only. The exception,
  `query_with_receipt`, is **off** unless the operator turns it on ([below](#query-results)). With it
  off, the one value dblens reads from a table is the migration id in EF Core's own history table
  (`compare_ef_model`, [below](#ef-cores-history-table)) — bookkeeping EF writes, never application
  data.
- **No cloud.** The server runs locally over stdio; nothing leaves the machine except the tool results
  the MCP client asked for.
- **Caller SQL runs only on a read-only login** when the database is on another machine. Enforced,
  not advised ([below](#3-read-only-login)); a database on this machine is exempt.

## What can execute

| Path | Who writes the SQL | Returns | Default |
|---|---|---|---|
| `explain_query` with `analyze: true` | the caller | a plan with real row counts | off per call |
| `query_with_receipt` | the caller | up to 100 rows + receipt | **disabled** by server policy |
| `analyze_denormalization` with `verifySignals: true` | dblens, from the catalog | two counts per probe | off per call |
| `infer_relationships` / `build_glossary` with verification | dblens, from the catalog | two counts per probe | off per call |
| `find_hot_queries` with `verifyJoins: true` | dblens, from the catalog | two counts per probe | off per call |
| `analyze_table_hygiene` with `verifySignals: true` | dblens, from the catalog | two counts per probe | off per call |

Everything else reads the catalog, statistics views or estimated plans, which execute nothing — and,
for `compare_ef_model`, the `MigrationId` column of the EF history table.

## Layers, outermost first

### 1. Statement guard

`Analysis/QueryPlan/StatementGuard.cs` runs in `DatabaseInspectorBase` before any connection opens,
for every statement that will execute — caller SQL and dblens's own probes alike. It rejects:

- anything but one `SELECT` (a leading `WITH` is fine); a second statement after `;`;
- every writing or state-changing keyword anywhere in the text: DML, DDL, `MERGE`, transaction
  control, `SET`, `EXEC`, `COPY`, `INTO`, data-modifying CTEs…; on SQL Server, where a second
  statement needs no `;`, also `DENY`, `RECONFIGURE`, `WRITETEXT`, `RAISERROR`, `… TRIGGER`;
- functions with side effects, wherever they are called: `pg_terminate_backend`, `pg_cancel_backend`,
  `pg_reload_conf`, file access (`pg_read_file`, `pg_ls_*`, `lo_import`…), sequences (`nextval`,
  `setval`, `NEXT VALUE FOR`), `set_config`, advisory locks, `pg_sleep`, statistics resets
  (`pg_stat_reset*`), `query_to_xml`, `dblink`, `OPENQUERY`, `OPENROWSET`, `OPENDATASOURCE`,
  `xp_*` / `sp_*`;
- locking clauses (`FOR UPDATE`, `FOR SHARE`, `WITH (UPDLOCK)`, `TABLOCKX`, `HOLDLOCK`…);
- four-part names, which address a linked server;
- PostgreSQL Unicode-escaped identifiers (`U&"…"`), whose names cannot be read without decoding.

Comments and string literals are removed before the scan, so `-- delete` or `'pg_sleep(1)'` change
nothing; quoted identifiers are checked too, so `"pg_terminate_backend"(1)` is caught. A column that
happens to be named like a keyword (`comment`, `lock`) is rejected unquoted and accepted quoted.

The text is split into code, comments, strings and identifiers **by the target engine's own rules**
(`SqlText.Segments`): dollar-quoted and `E''` strings and `[` as a subscript on PostgreSQL,
`[bracketed]` names on SQL Server, nested block comments on both. A guard that ends a string
somewhere the engine does not can be shown one statement while the engine runs two, so every
difference found goes into `StatementGuardLexingTests`. On PostgreSQL the transaction caller SQL
runs in pins `standard_conforming_strings = on`, the setting those rules assume.

An estimated plan (`explain_query` with `analyze: false`) runs nothing, so it accepts any statement —
that is how a write is explained — but still exactly **one**: a second statement after `;` would be
sent and executed, and is rejected. On PostgreSQL the `EXPLAIN` itself runs in the read-only,
rolled-back transaction below.

**The guard is a filter, not a proof.** It cannot see what a user-defined function does or which
privileges a login holds. That is why the next two layers exist.

### 2. Always-rolled-back transaction

Every executed statement — and, on PostgreSQL, every `EXPLAIN` — runs in its own transaction that is
never committed.

| | PostgreSQL | SQL Server |
|---|---|---|
| Caller SQL | `READ ONLY`, `statement_timeout` 30 s | `READ COMMITTED`, `LOCK_TIMEOUT` 5 s, command timeout 30 s |
| dblens probes | `READ ONLY`, `statement_timeout` 10 s | `READ UNCOMMITTED`, `LOCK_TIMEOUT` 5 s, command timeout 10 s |

Every other command dblens sends — catalog reads included — has a 30 s command timeout.

On PostgreSQL `READ ONLY` makes the engine itself refuse writes. SQL Server has no read-only
transaction, so there the guard and the login are what keep a write from being sent; results say so.
Probes on SQL Server read uncommitted so a scan cannot block production writers (the result notes the
trade-off); caller SQL does not, because a figure someone is asked to trust cannot come from a dirty
read.

### 3. Read-only login

Before executing caller SQL, dblens asks the database whether the connected login could change data,
and refuses if so:

- **PostgreSQL** — superuser, or `INSERT` / `UPDATE` / `DELETE` / `TRUNCATE` on any user table
  (`has_table_privilege`, which folds in ownership and role membership).
- **SQL Server** — `sysadmin`, database-level write / `ALTER` / `CONTROL` / `EXECUTE`, or
  `INSERT` / `UPDATE` / `DELETE` / `EXECUTE` on any user object (`HAS_PERMS_BY_NAME`, effective
  permissions).

The refusal names the reason. Estimated plans, catalog reads and the diagnostic tools still work on
any login.

**Local databases are exempt.** When every server in the connection string is this machine —
`localhost`, a loopback address, a PostgreSQL socket directory, `.`, `(local)`, LocalDB, a local named
pipe or the machine's own name — the check is skipped: that is a developer's own instance (the demo
containers included), where the admin login is the normal one. The test is on the address only; no
DNS lookup is made, so an alias that happens to resolve to `127.0.0.1` still counts as remote.

An address says nothing about what sits behind it: `ssh -L`, `kubectl port-forward` and a cloud SQL
proxy make a production database answer on `localhost`. So when the exemption lets through a login that
can write, `explain_query` (with `analyze`) and `query_with_receipt` say it in a caveat that names the
reason and the tunnel case; on SQL Server it adds that the rollback does not undo the locks the
statement holds while it runs. The audit skill asks before accepting such a login. The switch is the
existing `DbLens:ReadOnlyLoginOnLocalhost=true`: with it a write-capable login is refused wherever it is.

| Setting | Effect |
|---|---|
| *(default)* | check remote servers, skip local ones |
| `DbLens:ReadOnlyLoginOnLocalhost=true` | check local servers too — set it when dblens runs **on** a real database server, where "local" is production |
| `DbLens:AllowWriteCapableLogin=true` | never check; for a disposable remote database only, never against shared data |

#### Creating the login

The demo databases ship one (`db/*/03-reader.sql`); mirror it in production.

```sql
-- PostgreSQL 14+
CREATE ROLE dblens_reader LOGIN PASSWORD '…';
GRANT pg_read_all_data, pg_monitor TO dblens_reader;   -- pg_monitor: stats, pg_stat_statements, pgstattuple
-- optional: lets unused-index see when the database was created or restored
GRANT EXECUTE ON FUNCTION pg_stat_file(text) TO dblens_reader;
```

```sql
-- SQL Server
CREATE LOGIN dblens_reader WITH PASSWORD = '…';
GRANT VIEW SERVER STATE TO dblens_reader;              -- server start, for unused-index
USE your_database;
CREATE USER dblens_reader FOR LOGIN dblens_reader;
ALTER ROLE db_datareader ADD MEMBER dblens_reader;
GRANT VIEW DATABASE STATE, VIEW DEFINITION, SHOWPLAN TO dblens_reader;
```

`VIEW DEFINITION` lets dblens read view and procedure bodies for join evidence; `VIEW DATABASE STATE`
covers index usage, fragmentation and Query Store.

## Query results

`query_with_receipt` returns what the query selects, so it is a different contract from the rest of
dblens. It is disabled unless the server is started with `DbLens:AllowQueryResults=true` (in
`appsettings.json`, or the environment variable `DbLens__AllowQueryResults=true`). It is a server
setting on purpose: a model cannot turn it on.

When enabled:

- the statement passes all three layers above;
- at most 100 rows are returned, strings cut at 500 characters; a value that cannot be read or
  serialized is reported as text, never as a failure of the whole query;
- the description tells the model to select aggregates rather than personal data. That is an
  instruction, not an enforcement — masking personal values is not implemented, so enable this only
  where the model may see the data the login can read.

`certify_query` stores SQL only after planning it (`EXPLAIN`, nothing executed), so certifying needs
no query results.

## Signal verification

Probes are generated by dblens from catalog names, never taken from the caller:

- **Aggregate only.** Each probe selects two counts — rows compared, rows diverging — and never a
  value.
- **Quoted identifiers.** Every name goes through `SqlIdentifier`, so a hostile table or column name
  cannot inject SQL or a keyword.
- **Bounded.** At most 20 probes per call, 10 s each, 30 s in total, no table over 5 million rows or
  with an unknown row count — the same budget for `analyze_denormalization`, `infer_relationships`,
  `build_glossary`, `find_hot_queries` and `analyze_table_hygiene`. These are server defaults, not tool parameters, so a model cannot widen them.
  What is not run stays a signal, carries the reason in `Metadata["verification"]`, and is named in a
  caveat that states the limit it hit.
- **Failures don't propagate.** A probe that fails leaves its signal unverified and adds a caveat.
- **Not self-confirming.** dblens's probes appear in `pg_stat_statements` / Query Store too; they are
  excluded when the query history is read as join evidence.

### How a measurement is reported

- A measurement promotes only the statement it measured. The title states the count, and
  `Confidence: 1.0` applies to that count; the diagnosis sits under `WHAT THIS MAY MEAN:` and is not
  certified by it — a deliberate snapshot and a stale cache give the same number.
- Zero rows compared is `Inconclusive`, never `Refuted`. A refuted signal is still a fact, at `Info`.
- Rule IDs never change with verification; the state lives in which list a result is in, in
  `Evidence`, and in `Metadata["verification"]`.

## Files dblens reads

`compare_ef_model`, and `analyze_table_hygiene` when given `modelSnapshotPath`, read an EF Core
migrations snapshot from a path the caller gives. They are the only tools that open a local file,
held to the least they need:

- only files named `*ModelSnapshot.cs` and `*.Designer.cs` are opened, at most 16 MB each and 5,000
  designer files; the path must be absolute. The designer files are looked for under the folder
  above the snapshot's, to find the other DbContexts' migrations, skipping `bin`, `obj`,
  `node_modules` and `.git`;
- the files are parsed as C# syntax and **never compiled, loaded or run** — no build, no reflection
  over the user's assemblies;
- nothing of the file is returned but the model's own names (entities, properties, tables, columns,
  migration ids).

### Named connections

`DbLens:ConnectionsPath` holds connections saved under a name, one `<name>.conn` file each. The
server reads them; a call passes only the name (`connection`), so a password written there by a
local tool never appears in a prompt, a tool argument or a result. The audit skill writes the file
from a Docker container's own environment with a command that prints no secret, and keeps it
readable by the user only (`0600`).

### The plugin's one command

The server never builds the user's code. The optional Claude Code plugin can, once: its `ef` agent
runs the `dotnet ef migrations script` command `compare_ef_model` returns, which builds the project
and writes SQL to a file, only when the audit is started with `efScript: true` — the user's consent,
asked for first. The agent's Bash is limited to that command (`tools:`), and the command is built
only from migration ids and a context name shaped like EF's (letters, digits, `_`). The script is
then read by `check_migration_safety`, which never executes it.

### EF Core's history table

On the database side `compare_ef_model` makes the catalog reads every other tool makes, plus one read
of row values: `SELECT "MigrationId" FROM <history table>`. It is the table EF Core keeps to record
which migrations ran (`__EFMigrationsHistory` unless `MigrationsHistoryTable` renames it), found by
that name; only the id column is read, and the ids come back in `Migrations` and in the findings. No
other table's rows are read, and no operator setting is needed: the ids are names EF generated, the
same ones the code's migration files carry.

## DbLens.Docs reads source code

`DbLens.Docs` (the project inventory) is a separate package because it asks for a different permission:
DBLens opens only `*ModelSnapshot.cs` and `*.Designer.cs`, DbLens.Docs reads a solution. The user chooses
to give it, by pointing it at a folder. It opens:

- `*.cs`, `*.csproj`, `Directory.Build.props` and `Directory.Packages.props`;
- `appsettings*.json`, of which **only the key names** are kept: a value in a settings file may be a
  secret, and the inventory never holds one (a test over eShop's settings, which carry placeholder
  passwords, checks it);
- the EF Core snapshots, through `SnapshotReader`, as DBLens already does.

Every file is parsed as syntax (Roslyn) or XML and **never compiled, loaded or run**: no MSBuild
evaluation, no restore, no analyzer, no reflection over the user's assemblies. `bin`, `obj`,
`node_modules`, `.git`, `.vs` and `.idea` are not entered; links are not followed; a file over 2 MB is
not read and an inventory stops at 50,000 files, each said in the inventory's `coverage`. It connects to
no database: the data model comes from the EF snapshots alone.

`dblens-docs scaffold` and `confirm` **write**: only the five known Markdown files of the docs folder, which
must be inside the root, never through a link (neither the files nor any folder of the docs path below the
root), each through a temporary file with a name that cannot already exist; two writes of the MCP server never
overlap (the command-line `scaffold` and `confirm` are other processes and are not part of that lock); `check` and `inventory` write
nothing (but `--out`).

`dblens-docs mcp` serves the same folder to a model, and **fixed when it starts**: no tool takes a path.
Three tools read; `set_doc_slot` writes the words of one slot of those five files, and nothing else. It
refuses a slot that holds a person's words (also when the item has since changed), a document whose item
changed since it was generated, more lines than the slot allows, a line over 300 characters, blank
lines, and any text with an HTML comment, which is how the regions are marked. The words are marked
`inferred`; a person editing them makes them `human`. A model cannot confirm, delete or empty a slot.

What the inventory carries is names and text the code states: project names and references, package ids
and versions, route templates, configuration keys, the class names of background services, the
base-address text of an HTTP client when it is a literal. Never a settings value, never a secret.

## Planner statistics

`analyze_table_hygiene` reads what the planner knows of a column, not the column. On PostgreSQL that
is `pg_stats.null_frac` and `n_distinct`; `most_common_vals` and `histogram_bounds`, which hold values,
are never selected. On SQL Server the histogram of `sys.dm_db_stats_histogram` holds values in
`range_high_key`: the query only counts its steps and tests the keys for NULL, so a constant column's
one value never leaves the database. A test checks that it is in no result.

## Workload text

`find_hot_queries` is the one tool that returns SQL text taken from the database's workload. Query
Store keeps the literals of queries that were not parameterized, and any query can carry a comment,
so the text leaves only through `SqlText.Redact`: string and number literals become `?`, comments
are removed, read with the same lexer as the statement guard (`SqlText.Segments`), so a string or a
comment ends where the engine ends it. The filters of a stored plan are redacted the same way. A
test runs a workload whose literals and comment hold a name, an e-mail and an amount, and checks
that none appears anywhere in the result.

Nothing of the workload is executed. Plans are the ones SQL Server stored, or PostgreSQL's generic
plan of the normalized text: `PREPARE` and `EXPLAIN EXECUTE`, without `ANALYZE`, in the read-only
transaction caller SQL gets, after `IsSingleStatement`.

## Join evidence from SQL text

`infer_relationships` and `build_glossary` read view and routine definitions and, when available, the
query history. String literals are blanked before parsing and no SQL text is returned — only the
names of the objects a join was seen in — so a logged query holding personal data in a `WHERE` clause
contributes table and column names only.

## Results that could read as an all-clear

| Tool | Empty result means | What it reports instead |
|---|---|---|
| `scan_gdpr` | no column *name* matched | `Coverage`: columns it could not interpret, plus an explicit caveat |
| `analyze_fragmentation` | maybe nothing was measured | `StatsAvailable: false` and the reason |
| `check_migration_safety` | maybe nothing was recognized | statements of kind `Unknown` |
| any scoped tool | maybe the schema or table name matched nothing | a caveat naming what was not found |
| `check_cascade_paths` | — | an unknown `startingTable` is an error |
| `analyze_indexes` (`unused-index`) | maybe counters were just reset or the dump just restored | the counting window and its start; rule skipped under 7 days |
| `check_cascade_paths` | no *declared* cascade | disclaimer: ORM cascades and triggers are invisible |
| `infer_relationships` | maybe no query history | caveat when pg_stat_statements / Query Store is unavailable |
| `find_hot_queries` | maybe no statistics to read | `Availability`: the source read, its window, and what each missing source needs |
| `query_with_receipt` | the figure is unconfirmed | caveat when no cross-check ran; `Disagree` when one failed |

Every tool description also says explicitly what the response does **not** contain, so the model does
not fill the gap by guessing.
