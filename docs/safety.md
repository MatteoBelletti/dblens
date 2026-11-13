# Safety model

dblens is pointed at real databases by a language model. This page states the contract, everything
that can execute, and what stops each of those things from doing harm.

## The contract

- **No writes.** No tool changes the database. Proposed DDL is returned as text (`SqlFix.Kind = Ddl`)
  for a human to review.
- **No row data — by default.** Every tool returns metadata, plans and counts only. The single
  exception, `query_with_receipt`, is **off** unless the operator turns it on
  ([below](#query-results)). With it off, dblens never returns a value from the data.
- **No cloud.** The server runs locally over stdio; nothing leaves the machine except the tool results
  the MCP client asked for.
- **Caller SQL runs only on a read-only login** when the database is on another machine. Enforced,
  not advised ([below](#read-only-login)); a database on this machine is exempt.

## What can execute

| Path | Who writes the SQL | Returns | Default |
|---|---|---|---|
| `explain_query` with `analyze: true` | the caller | a plan with real row counts | off per call |
| `query_with_receipt` | the caller | up to 100 rows + receipt | **disabled** by server policy |
| `analyze_denormalization` with `verifySignals: true` | dblens, from the catalog | two counts per probe | off per call |
| `infer_relationships` / `build_glossary` with verification | dblens, from the catalog | two counts per probe | off per call |

Everything else reads the catalog, statistics views or estimated plans, which execute nothing.

## Layers, outermost first

### 1. Statement guard

`Analysis/QueryPlan/StatementGuard.cs` runs in `DatabaseInspectorBase` before any connection opens,
for every statement that will execute — caller SQL and dblens's own probes alike. It rejects:

- anything but one `SELECT` (a leading `WITH` is fine); a second statement after `;`;
- every writing or state-changing keyword anywhere in the text: DML, DDL, `MERGE`, transaction
  control, `SET`, `EXEC`, `COPY`, `INTO`, data-modifying CTEs…;
- functions with side effects, wherever they are called: `pg_terminate_backend`, `pg_cancel_backend`,
  `pg_reload_conf`, file access (`pg_read_file`, `lo_import`…), sequences (`nextval`, `setval`),
  `set_config`, advisory locks, `pg_sleep`, `query_to_xml`, `dblink`, `OPENQUERY`, `OPENROWSET`,
  `OPENDATASOURCE`, `xp_*` / `sp_*`;
- locking clauses (`FOR UPDATE`, `FOR SHARE`, `WITH (UPDLOCK)`, `TABLOCKX`, `HOLDLOCK`…);
- four-part names, which address a linked server.

Comments and string literals are removed before the scan, so `-- delete` or `'pg_sleep(1)'` change
nothing; quoted identifiers are checked too, so `"pg_terminate_backend"(1)` is caught.

**The guard is a filter, not a proof.** It cannot see what a user-defined function does or which
privileges a login holds. That is why the next two layers exist.

### 2. Always-rolled-back transaction

Every executed statement runs in its own transaction that is never committed.

| | PostgreSQL | SQL Server |
|---|---|---|
| Caller SQL | `READ ONLY`, `statement_timeout` 30 s | `READ COMMITTED`, `LOCK_TIMEOUT` 5 s |
| dblens probes | `READ ONLY`, `statement_timeout` 10 s | `READ UNCOMMITTED`, `LOCK_TIMEOUT` 5 s |

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
```

```sql
-- SQL Server
CREATE LOGIN dblens_reader WITH PASSWORD = '…';
GRANT VIEW SERVER STATE TO dblens_reader;              -- uptime for unused-index
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
- at most 100 rows are returned, strings cut at 500 characters;
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
  with an unknown row count. What is not run is reported as skipped, with the reason.
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
| `analyze_indexes` (`unused-index`) | maybe counters were just reset | uptime caveat; rule skipped under 7 days |
| `check_cascade_paths` | no *declared* cascade | disclaimer: ORM cascades and triggers are invisible |
| `infer_relationships` | maybe no query history | caveat when pg_stat_statements / Query Store is unavailable |
| `query_with_receipt` | the figure is unconfirmed | caveat when no cross-check ran; `Disagree` when one failed |

Every tool description also says explicitly what the response does **not** contain, so the model does
not fill the gap by guessing.
