# Tool reference

Every tool dblens exposes, with its parameters, its rules and what it does **not** tell you.
For the terms used here (finding, signal, evidence, probe…) see the [glossary](../GLOSSARY.md).

All database tools share these parameters, so the tables below leave them out:

| Parameter | Type | Default | Meaning |
|---|---|---|---|
| `dialect` | `PostgreSql` \| `SqlServer` | `PostgreSql` | Target engine |
| `connectionString` | string | server default | Use a different database for this call only |

Scoped tools also take `schema` (null = every user schema) and `tables` (bare names, null = every
table in scope): `analyze_indexes`, `analyze_fragmentation`, `analyze_denormalization`, `scan_gdpr`,
`infer_relationships` and `build_glossary`. `check_cascade_paths` takes `schema` only, and starts from
`startingTable` instead. A schema or table that matches nothing is named in a caveat: an empty result
for a mistyped name is not a clean one. An empty `connectionString` means the server default.

Thresholds, budgets and caps are server defaults, not parameters: a model has no reason to tune them,
and the result states the ones that applied.

| Tool | Reads | Executes SQL? | Results in |
|---|---|---|---|
| [`ping`](#ping) | nothing | no | — |
| [`get_schema`](#get_schema) | catalog | no | overview |
| [`get_table_details`](#get_table_details) | catalog | no | structure |
| [`analyze_indexes`](#analyze_indexes) | catalog + scan counters | no | Findings |
| [`analyze_fragmentation`](#analyze_fragmentation) | physical stats | no | Findings |
| [`check_cascade_paths`](#check_cascade_paths) | catalog | no | Findings |
| [`compare_ef_model`](#compare_ef_model) | an EF Core snapshot (as text) + catalog | no | Findings |
| [`analyze_table_hygiene`](#analyze_table_hygiene) | planner statistics, catalog, update counters (+ an EF snapshot as text) | only with `verifySignals: true`, counts only | Findings + Signals |
| [`find_hot_queries`](#find_hot_queries) | the engine's query statistics + estimated or stored plans | only with `verifyJoins: true`, counts only | Findings + Signals |
| [`check_migration_safety`](#check_migration_safety) | catalog (table sizes) | **never** the script | Findings |
| [`explain_query`](#explain_query) | plan | only with `analyze: true`, pure `SELECT`, rolled back | Findings |
| [`scan_gdpr`](#scan_gdpr) | column names | no | Signals only |
| [`analyze_denormalization`](#analyze_denormalization) | catalog | only with `verifySignals: true`, counts only | Signals → Findings |
| [`infer_relationships`](#infer_relationships) | catalog, views, routines, query history | only with `verifyCandidates: true`, counts only | Signals → Findings |
| [`build_glossary`](#build_glossary) | as `infer_relationships`, plus comments | only with `verifyJoins: true`, counts only | a SKILL.md |
| [`query_with_receipt`](#query_with_receipt) | the data | **yes** — disabled unless the operator enables it | rows + receipt |
| [`certify_query`](#certify_query) / [`list_certified_queries`](#certify_query) | plan only | no | local file |

Caller SQL (`explain_query` with `analyze`, `query_with_receipt`) only runs on a read-only login when
the server is on another machine; see [safety.md](safety.md#3-read-only-login).

Every diagnostic result has the same skeleton: `Findings`, `Signals`, a `Summary` whose `BySeverity`
and `ByRule` dictionaries always contain every key, `Caveats`, and — where relevant — a `Disclaimer`.
Results are capped at 200 findings in total; facts are kept in preference to signals, the most severe
first within each, and a caveat says how many were cut. The `Summary` counts what is returned.

---

## `ping`

Returns the server version, a UTC timestamp, the supported dialects and `message` echoed back
(default `pong`). Touches no database: use it to check the server is reachable.

## `get_schema`

Lists tables and views with row estimates, sizes and FK/index counts, grouped by schema.

| Parameter | Default | Meaning |
|---|---|---|
| `schemaFilter` | — | Exact schema name |
| `namePattern` | — | SQL `LIKE` pattern on object names, e.g. `order%` |
| `includeViews` | `true` | Include views |
| `includeSystemSchemas` | `false` | Include `pg_catalog`, `information_schema`, `sys`… |

**Not included:** column names. Each object carries only a `ColumnCount`; call `get_table_details`
for the columns.

On PostgreSQL a partitioned table is one table whose rows and size are those of its partitions; each
partition is listed too, with `PartitionOf` naming its parent, and is not counted again in the totals.
Materialized views are listed as views. The structural diagnostics read the partitioned table and
skip its partitions unless one is named in `tables`; `analyze_fragmentation` reads the partitions,
which is where the pages are.

## `get_table_details`

Columns (type, nullability, default), indexes with their definitions, foreign keys in both
directions, check and unique constraints, for 1–20 tables or views.

| Parameter | Default | Meaning |
|---|---|---|
| `tables` | required | `schema.name` or `name` |
| `defaultSchema` | `public` / `dbo` | Schema for unqualified names |

A name is written without quotes or brackets. When it contains dots (`my.schema.orders`) every split
into schema and table is tried and the catalog decides; on SQL Server names are matched by the
database's collation, so `USERS` finds `users` where the database is case-insensitive.

An index reports its key in `Columns`, the key columns sorted descending in `DescendingColumns` and
the columns it only carries in `IncludedColumns`; a column
computed by the database (generated, computed, system-period) has `IsGenerated`. `Kind` is `table`,
`view` or `materialized view`.

**Not included:** view bodies, row data, anything about names listed in `NotFound`.

## `analyze_indexes`

| Parameter | Default | Meaning |
|---|---|---|
| `includeUnusedIndexes` | `true` | Run `unused-index`, which reads scan statistics |

| Rule | Severity | Evidence | Detects |
|---|---|---|---|
| `fk-without-index` | High | Derived | FK columns with no index that leads with them |
| `duplicate-index` | Medium | Derived | Two indexes with the same key and key order, uniqueness, access method and `INCLUDE` columns |
| `redundant-index` | Low | Derived | A non-unique index whose work another does: its key is a prefix of the other's, or the same key under a unique or wider-covering index |
| `unused-index` | Low | Measured | A non-unique index with zero scans |
| `fk-type-mismatch` | Medium / Low | Derived | A foreign key column whose type is not the referenced key's: Medium when it is narrower (`integer` referencing `bigint`, a shorter `varchar`), so a later parent key cannot be referenced. PostgreSQL only: SQL Server refuses such a key |

Only an index that can do the job counts. A partial (filtered) index never covers a foreign key or
makes another index redundant; a hash index covers a foreign key only on exactly its columns; GIN,
GiST and columnstore indexes are not compared at all. An index is only called redundant when the
other one also holds every column it carries. Key order counts: `(a, b DESC)` is neither a duplicate
nor a prefix of `(a, b)`, while `(a DESC, b DESC)` is the same index read backwards. Disabled and hypothetical SQL Server indexes are ignored.

Every finding has a `SqlFix` with the `CREATE INDEX` / `DROP INDEX` statement (kind `Ddl`), written
not to block writes: `CONCURRENTLY` on PostgreSQL, `ONLINE = ON` on SQL Server (Enterprise or Developer
edition; on Standard drop the option and schedule a window).
`unused-index` needs at least 7 days of scan counts. The counting window starts at the latest of: the
server start, the database's creation, its last restore, and a statistics reset — so a dump restored
yesterday on a server up for a month has one day, and the rule is skipped. Each finding carries the
window in `Metadata` (`stats_window_days`, `stats_since`), and a caveat states it.

| Engine | Creation | Restore | Reset |
|---|---|---|---|
| SQL Server | `sys.databases.create_date` | `msdb.dbo.restorehistory`, which also sees `RESTORE … WITH REPLACE` | — |
| PostgreSQL | the `PG_VERSION` file, read only with `EXECUTE` on `pg_stat_file` | a restore is a new database: its creation | `pg_stat_database.stats_reset` |

Without `pg_stat_file` a PostgreSQL restore made after the server start cannot be seen: the window is
dated from the start, findings drop to confidence 0.6, and a caveat says so. A `pg_restore --clean`
into an existing database changes none of these dates; for that, and any restore the dates miss,
the rule is skipped with a caveat when no index of the database has recorded a single scan — the
workload has not run there, so every index would look unused. PostgreSQL 15+ keeps
statistics across a clean restart, so dating from the start only ever shortens the window. A login
that cannot read the counters (`VIEW SERVER STATE` on SQL Server) gets the other three rules and a
caveat.

## `analyze_fragmentation`

| Parameter | Default | Meaning |
|---|---|---|
| `includePageDensity` | `false` | Also measure page density. On SQL Server this switches the scan from `LIMITED` to `SAMPLED` |

| Rule | Severity | Detects |
|---|---|---|
| `idx-fragmentation-high` | Medium | ≥ 30 % fragmentation — rebuild (`REINDEX CONCURRENTLY` / `ALTER INDEX … REBUILD`) |
| `idx-fragmentation-moderate` | Low | ≥ 10 % — reorganize on SQL Server; rebuild on PostgreSQL, which has no reorganize |
| `idx-low-page-density` | Low | Pages under 60 % full |

Indexes under 1 000 pages (~8 MB) are skipped and counted in the summary.

**Requirements:** PostgreSQL needs the `pgstattuple` extension and the right to call `pgstatindex`
(`pg_stat_scan_tables`, part of `pg_monitor`), and only btree indexes are measured; SQL Server needs
`VIEW DATABASE STATE`. When the statistics cannot be read the result says `StatsAvailable: false`
with an empty `Findings` list — that means *not measured*, not *healthy*.

**Not considered:** fill factor, page-split history, how fast an index re-fragments, whether anything
range-scans it. This is one input to a maintenance decision. For a maintenance strategy use
`sp_BlitzIndex` / Ola Hallengren's scripts (SQL Server) or PgHero / pganalyze (PostgreSQL).

## `check_cascade_paths`

| Parameter | Default | Meaning |
|---|---|---|
| `startingTable` | — | Qualified table, e.g. `public.users`. Omitted: every table targeted by an `ON DELETE CASCADE` key. A table that does not exist is an error, not an empty result |
| `maxDepth` | `10` | Hard cap 50 |
| `schema` | — | Restrict the cascade roots to one schema |

| Rule | Severity | Detects |
|---|---|---|
| `cascade-circular-path` | Critical | A cycle in the cascade graph |
| `cascade-deep-path` | High | A chain deeper than 5 levels |
| `cascade-breaks-with-restrict` | High | A `NO ACTION` / `RESTRICT` key that makes the DELETE fail at runtime |
| `cascade-breaks-with-set-null` | Info | The chain turns into `SET NULL`: rows survive, detached |
| `cascade-self-referencing` | Info | A self-referencing `CASCADE` key (a tree) |
| `cascade-summary` | Info | Always exactly one: paths, max depth, tables reached |

Each path carries the approximate row count of every table it reaches. At most 500 paths are
followed; beyond that `PathsTruncated` is true and a caveat says so. Only declared foreign keys are
analyzed: ORM cascades and triggers are invisible.

A `NO ACTION` key is checked when the statement ends, so it does not block when a `CASCADE` key from
the same table deletes every row that references the parent through it (`user_id` cascades, and
`(tenant_id, user_id)` is `NO ACTION`): such a key is not reported. A key on another column
(`approved_by` beside `created_by`) keeps rows the cascade does not reach and is reported, as is
every `RESTRICT` key, which is checked at once.

## `check_migration_safety`

| Parameter | Default | Meaning |
|---|---|---|
| `ddl` | required | The script; several statements allowed |
| `defaultSchema` | `public` / `dbo` | Schema for unqualified names |

| Rule | Severity | Detects |
|---|---|---|
| `migration-table-rewrite` | Critical / Medium | Type changes and volatile defaults that rewrite every row |
| `migration-blocking-lock` | High / Medium | Locks that block traffic, `NOT NULL` additions, renames that break running code |
| `migration-index-not-concurrent` | High / Medium | Index builds without `CONCURRENTLY` / `ONLINE = ON` |
| `migration-validated-constraint-scan` | High / Low | FK and check constraints validated against every existing row |
| `migration-destructive` | Critical | `DROP TABLE`, `DROP COLUMN`, `TRUNCATE`, `DROP SCHEMA` / `DROP DATABASE` |
| `migration-summary` | Info | Always exactly one |

For an EF Core migration, pass the script `dotnet ef migrations script` writes — the command
`compare_ef_model` gives — without `--idempotent`: the idempotent script wraps every statement in a
`DO` block (PostgreSQL) or `IF NOT EXISTS` (SQL Server), which this tool does not see into. In the
plain script, `START TRANSACTION` and the `INSERT` into the history table are listed as `Unknown`.

`ALTER TABLE … VALIDATE CONSTRAINT` (the second half of PostgreSQL's `NOT VALID` pattern) is recognized
and raises nothing: it scans without blocking reads or writes.

Severity is weighted by the real size of the table: the same statement is Critical on 50 million rows
and Medium on a lookup table (threshold: 1 million rows). The script is **never executed**.
Statements are matched by shape, not parsed: a statement of kind `Unknown` was *not recognized*,
which is not the same as safe. `UPDATE`, `DELETE` and anything else that is not DDL are `Unknown`.

The script is cut into one statement per object changed: on `;`, and for SQL Server also on `GO`
and at each line that starts a new statement, since T-SQL needs no separator (a procedure, function,
trigger or view stays whole). An `ALTER TABLE` with several actions and a `DROP TABLE` with several
names yield one statement each. `BEGIN` / `COMMIT` are skipped.

## `explain_query`

| Parameter | Default | Meaning |
|---|---|---|
| `sql` | required | Exactly one statement |
| `analyze` | `false` | Execute it for real row counts and timings |

| Rule | Severity | Detects |
|---|---|---|
| `plan-seq-scan-large-table` | High / Medium | A full scan over more than 10 000 rows: rows read when the engine reports them (`analyze`), so a scan whose filter discards everything still counts; rows returned or estimated otherwise |
| `plan-estimate-mismatch` | High | Estimated and actual rows differ by 10× or more (`analyze` only) |
| `plan-nested-loop-large-input` | Medium | A nested loop driven by more than 10 000 outer rows |
| `plan-sort-spilled-to-disk` | Medium | A sort or hash that did not fit in memory |
| `plan-scan-with-filter` | Low | A predicate applied after reading every row |
| `plan-implicit-conversion` | Medium | A column converted to another type to be compared with a parameter or literal, so an index on it cannot be used. SQL Server reports it (`PlanAffectingConvert`, a finding); on PostgreSQL it is read from a cast in a sequential scan's filter (a signal: an expression index could still serve it), casts to `text` aside. An `nvarchar` conversion names the EF fix, `IsUnicode(false)` |

With `analyze: false` nothing runs and every row count is an optimizer estimate; any single statement
is accepted, a write included, but a second statement after `;` is rejected. With `analyze: true`
see the [safety model](safety.md#1-statement-guard).

## `scan_gdpr`

| Parameter | Default | Meaning |
|---|---|---|
| `excludeTables` | — | Bare table names to skip |
| `minConfidence` | `0.5` | Lower for recall, raise for precision |

| Rule | Severity | Detects |
|---|---|---|
| `pii-identifier` | Critical / High | Names, birth dates, national IDs (SSN, codice fiscale, passport) |
| `pii-contact` | Medium / Low | Email, phone, address (Medium); IP, username, session id (Low) |
| `pii-financial` | Critical | Card numbers, IBAN, CVV, bank accounts |
| `pii-special-category` | Critical | GDPR Art. 9: health, biometrics, religion, politics, ethnicity, orientation |
| `pii-location` | Low | Coordinates, geolocation |
| `missing-gdpr-controls` | Medium | A table with PII and no soft-delete or audit timestamps |

Patterns ship in English and Italian (`src/DbLens.Core/Analysis/Gdpr/Patterns/*.json`). A High finding drops
to Medium when the table has an encryption or hashing marker column.

A name is read in lower snake case, so `First Name`, `firstName`, `E-Mail` and `phone1` match as
`first_name`, `e_mail` and `phone`. A pattern marked `qualifiable` is also found inside a longer name
(`customer_email`, `telefono_casa`, `address_line1`) at 85 % of its confidence, unless the extra word
says the column is about the value rather than the value (`email_verified`, `address_id`). Patterns
whose words are common elsewhere (`via`, `pin`, `nome`, `condition`) match the whole name only.

**Everything is a signal**: `Findings` is always empty. No row is read, so an empty result does not
mean there is no personal data. The `Coverage` block counts the columns whose names carry no usable
signal (`c1`, `fld_07`, codenames) and lists examples; always report it with the results.

## `analyze_denormalization`

| Parameter | Default | Meaning |
|---|---|---|
| `verifySignals` | `false` | Measure the checkable signals against the data |

| Rule | Severity | Confidence | Detects | Verifiable |
|---|---|---|---|---|
| `denorm-repeating-group` | Medium (3+) / Low | 0.9 / 0.6 | `phone1`, `phone2`, `phone3` | **yes**: rows that fill every slot |
| `denorm-derived-column` | Medium | 0.6 | A cached child count (`order_count`) | **yes**, when one child table is the obvious source |
| `denorm-derived-column` | Low | 0.7 | A concatenation of siblings (`full_name`) | no |
| `denorm-duplicated-column` | Low | 0.55 | The same column on two FK-linked tables | **yes** |
| `denorm-multivalue-column` | Medium | 0.5 | Text named like a list (`tags`, `role_list`) | **yes**: values holding a delimiter |
| `denorm-wide-table` | Low / Info | 0.5 / 0.3 | More than 40 columns; Low when at least 70 % of them are nullable | no |

Sums (`total`, `amount`) are never verified: the catalog does not say which column they sum. A
column the database computes itself (generated, computed) is never reported as derived.

With `verifySignals: true`, see [signal verification](safety.md#signal-verification). A measured
signal moves into `Findings` with `Evidence: Measured`; its title states the count and its
interpretation goes under `WHAT THIS MAY MEAN:`. A refuted signal is still reported, as `Info`.
Zero rows compared is *not measured*, never *refuted*.

Every signal a probe was built for carries `Metadata["verification"]`: `Status`, `RowsCompared`,
`RowsDiverging`, `DivergenceRatio`, the exact `Query`, and `NotVerifiedReason` when nothing was
measured (`TableTooLarge`, `RowCountUnknown`, `QueryBudgetExhausted`, `TimeBudgetExhausted`,
`NoRowsCompared`, or the failure kind such as `PermissionDenied`). The `Verification` block of the
result counts the outcomes; it is `null` when verification was not requested — different from
verification finding nothing. Probes run within the [budget](safety.md#signal-verification), and a
caveat names every signal left unverified and why.

## `infer_relationships`

| Parameter | Default | Meaning |
|---|---|---|
| `verifyCandidates` | `false` | Count orphan values for each candidate |

Returns `Relationships`: every join path, each with an `Evidence` of its own:

| Evidence | Meaning |
|---|---|
| `Declared` | A real foreign key |
| `Inferred` | A name and type match, not checked |
| `Verified` | Checked: every non-null value has a parent |
| `VerifiedWithOrphans` | Checked: at most half the values have no parent — a real but dirty reference |

Unverified candidates are `Low` signals. Verified, a candidate with no orphans stays `Low` and
becomes a measured finding; one with at most half orphans becomes `Medium`; one with more than half is
dropped from the map and reported as an `Info` finding. Verification metadata, budget and caveats work
as in [`analyze_denormalization`](#analyze_denormalization). Candidates come from
`relationship-undeclared-fk`, strongest evidence first:

| Basis | Confidence | Example |
|---|---|---|
| `observed-join` | 0.8 (0.85 when the name agrees) | a view, routine or logged query joins `s.ship_to = a.id` |
| `same-name-as-parent-key` | 0.7 | `customer_id` → `customers.customer_id` |
| `id-name-matches-table` | 0.55 | `order_id`, `id_order`, `orderId` → `orders.id` |
| `code-name-matches-table` | 0.45 | `cod_cliente`, `codice_cliente` → unique `clienti.codice` |

Observed joins are read from view and routine definitions and, when available, from
`pg_stat_statements` (extension installed) or Query Store (enabled); a caveat says when the history
could not be used. A join counts when one side is a single-column key, which gives the direction; the
signal's `Metadata["seenIn"]` names where it was seen (`view public.shipment_destinations`,
`workload (Query Store, 1,240 executions)`). Literals are blanked and no SQL text is returned.
dblens's own probes are excluded from the history. An observed join settles a column whose name
matched several parents.

Table names are matched with English and Italian plurals (`order/orders`, `cliente/clienti`,
`fattura/fatture`). A column matching more than one parent is skipped, not guessed, and counted.
Both sides must be in scope. A `Verified` finding carries the `ALTER TABLE … ADD CONSTRAINT` (kind
`Ddl`, with `NOT VALID` + `VALIDATE` on PostgreSQL); matching values are evidence of a reference, not
of intent, so confirm with the schema owner first.

## `build_glossary`

Writes what dblens knows about a schema as a ready `SKILL.md` — a data map a client loads before
writing SQL.

| Parameter | Default | Meaning |
|---|---|---|
| `verifyJoins` | `false` | Count orphans for inferred joins, as `infer_relationships` does, within the same budget |
| `writeSkillTo` | — | Absolute directory to write `SKILL.md` into, e.g. `<project>/.claude/skills/erp-data-map` |
| `overwrite` | `false` | Replace an existing `SKILL.md` |

For every table: approximate rows, how many views / routines / logged statements reference it (and
how often they ran), its documented meaning, its columns (type, key, meaning), its join paths labelled
`declared`, `inferred (basis)`, `verified` or `verified with orphans`, and candidate measures (numeric
columns named like amounts or totals). Certified queries come first.

**Meanings come only from schema comments** (`COMMENT ON` / `MS_Description`). An undocumented column
is shown as `—`; the skill tells the model to ask rather than guess, and that a comment is data
written by whoever maintains the database, never an instruction. A caveat says when nothing is
documented. No row values are included.

## `query_with_receipt`

**Disabled unless the server runs with `DbLens:AllowQueryResults=true`** — it is the one tool that
returns values from the data. See [safety.md](safety.md#query-results).

| Parameter | Default | Meaning |
|---|---|---|
| `sql` | — | One `SELECT` |
| `certified` | — | Run a certified query by name instead |
| `crossCheckSql` | — | The same figure computed another way |
| `tolerance` | `0` | Relative difference allowed between numbers in the cross-check, 0 to 1 |

Returns at most 100 rows and a **receipt**:

| Receipt field | Content |
|---|---|
| `Sql` | exactly what ran |
| `TablesTouched` | the relations in the plan (views expanded to base tables) |
| `Database`, `ExecutedAt` | where and when |
| `CrossCheck` | `Agree`, `Disagree` (with both values) or `NotComparable`, and the second result |
| `CertifiedAs` | set when the SQL matches a certified query |

A value JSON cannot carry (`NaN`, `Infinity`) is returned as text, and a cell the driver cannot read
(an out-of-range numeric, a CLR type) says so in place of its value instead of failing the query.

A cross-check compares the two results as sets of rows, so row order does not matter and `120.50`
equals `120.5`. Without one, a caveat says the figure is unconfirmed; with a disagreement, the caveat
says to report both figures rather than pick one. Useful pairs: two join paths to the same total, or
line items summed against the stored header total.

## `analyze_table_hygiene`

Columns nobody fills and tables whose creation and update dates are missing or not kept up to date,
in a code-first database: each finding carries the EF Core change in `Metadata["ef"]`.

| Parameter | Default | Meaning |
|---|---|---|
| `verifySignals` | `false` | Confirm signals with probes: two counts each, never a value |
| `modelSnapshotPath` | — | A `*ModelSnapshot.cs`, or a folder with one per DbContext: names the entity and property in each fix, and enables `column-not-in-model`. Unreadable = a caveat, the rest runs |

| Rule | Severity | Evidence | Candidate → fact |
|---|---|---|---|
| `column-always-null` | Low | Inferred → Measured | Statistics see only NULLs → no row holds a value |
| `column-single-value` | Low | Inferred → Measured | Statistics see one distinct value → one value on at least 100 filled rows. The value is never reported |
| `column-abandoned` | Medium | Measured only | With `verifySignals`, a nullable column of a table with one creation date → no value on at least 1,000 rows of the last 90 days, while an older row holds one |
| `column-not-in-model` | Medium | Derived | A column of a table the EF model maps, mapped by no property of any context; tables holding a mapping dblens does not compare (JSON, TPT…) are skipped |
| `column-legacy-name` | Info | Inferred | A last name word such as `old`, `bak`, `tmp`, `vecchio` (`notes_old`), a first one such as `legacy`, `deprecated`, `tmp` (`legacy_code`; not `backup_codes`), or a comment saying deprecated / obsolete; `old_value` and `water_temp` are not |
| `audit-missing-created` | Low | Inferred | No creation date on a table of 1,000+ rows |
| `audit-missing-updated` | Medium if updated, else Low | Inferred | No update date; not reported on a table known to be insert-only |
| `audit-updated-not-maintained` | Medium | Inferred → Measured | Both dates on an updated table → no row's update date differs from its creation date by more than a second (at least 1,000 rows compared) |
| `audit-update-before-create` | Low | Measured only | With `verifySignals` → any row updated more than a second before it was created |
| `audit-created-nullable` | Low | Derived | The creation date accepts NULL |
| `audit-timestamp-without-timezone` | Low | Derived | `timestamp without time zone`; SQL Server `datetime`, `smalldatetime` (`datetime2`, EF Core's mapping of `DateTime`, is not reported) |
| `hygiene-summary` | Info | Derived | Always one |

Creation and update columns are recognized by name (English and Italian, in
`Analysis/Hygiene/audit_columns.json`), compared in lower snake case without a `_utc` suffix (`CreatedOnUtc` is `created_on`), and must be dates (SQL Server's
`timestamp` is `rowversion`, not one). Keys,
indexed, constrained and referencing columns, generated and identity columns are never candidates;
audit dates are not checked for a single value.

**Statistics, never values.** On PostgreSQL `pg_stats.null_frac` and `n_distinct`, after `ANALYZE`.
On SQL Server the histogram of a statistics object led by the column: only the number of its steps
and whether their keys are NULL are read — a column of NULLs has a histogram of no steps. SQL Server
keeps statistics only on columns that were indexed or filtered on; a filtered statistic is not read,
and a column with several is empty or constant only if all of them say so. A caveat counts the
columns neither engine has statistics for.

**Updates.** `pg_stat_user_tables.n_tup_upd` / `sys.dm_db_index_operational_stats`, read only when the
counting window of [`unused-index`](#analyze_indexes) covers 7 days and the workload has run: a zero
otherwise means nothing, so no table is taken as insert-only and a caveat says so; likewise when the
login cannot read the counters, or they hold nothing for a table. SQL Server's counter restarts when
a table's metadata leaves the cache, so there a zero is never read as insert-only.

**Measured checks.** Signals are measured before the checks that have none, each smallest table
first, within the shared [budget](safety.md#signal-verification). A check with too few rows (fewer than 100 filled rows, or
1,000 recent ones) is not measured, never refuted. `column-abandoned` and `audit-update-before-create`
have no signal: they appear only when a probe finds the defect.

**EF Core changes** in `Metadata["ef"]`: the entity and property when the snapshot maps the column,
and a one-line change; the full recipes are one caveat each, once per result: removing a column in two releases (drop the property and the generated `DropColumn` first, drop the
column once no running version reads it); `IAuditable` with a `SaveChangesInterceptor` and a
`.SetProperty` in every `ExecuteUpdate`; on a table with rows, a nullable `CreatedAt` — a
non-nullable `DateTime` or `HasDefaultValueSql("now()")` gives every existing row a false creation date.

## `find_hot_queries`

Finds the queries that weigh most on the database, from the statistics the engine keeps, without
being given one: then reads their plans and measures the joins that multiply rows.

| Parameter | Default | Meaning |
|---|---|---|
| `verifyJoins` | `false` | Measure each join of one parent to two children with an aggregate probe |

| Engine | Source, in order | Read |
|---|---|---|
| PostgreSQL | `pg_stat_statements` of the current database, summed per `queryid` over users | calls, total time, rows, pages from cache and from outside it, temp pages |
| SQL Server | Query Store, then the plan cache (`sys.dm_exec_query_stats`); either summed per `query_hash` | executions, duration, logical and physical reads, rows, tempdb (Query Store only) |

The 20 heaviest by total time are listed: only statements that read or write rows (`SELECT`, `WITH`,
`INSERT`, `UPDATE`, `DELETE`, `MERGE`) and name at least one user table in a table position (after
`FROM`, `JOIN`, `UPDATE`, `INTO`, `USING`, `MERGE`, or a comma of a `FROM` list): a column sharing a
table's name does not count, a bare name in several schemas is the default schema's, and names that
differ only in case are told apart by it (unquoted, PostgreSQL folds to lower case). DDL, utility statements,
catalog queries and dblens's own probes are set aside. Each listed query carries its id, its text
**redacted** — every string and number literal replaced by `?`, every comment removed, at most 2,000
characters — its calls, total and mean time, rows per call, pages per row, share read outside the
cache, temp pages, and the tables it names.

| Rule | Severity | Evidence | Detects |
|---|---|---|---|
| `hot-query-reads-per-row` | Medium | Measured | More than 100 pages read per row returned, over at least 2 calls |
| `hot-query-disk-reads` | Low | Measured | 20% or more of 1,000+ pages read outside the database's cache (on PostgreSQL, possibly from the OS cache) |
| `hot-query-temp-spill` | Medium | Measured | Pages written to temporary files; on SQL Server tempdb space, temp tables included |
| `plan-*` of [`explain_query`](#explain_query) | as there | as there | On the plan of the first 10, each with `Metadata["queryId"]` |
| `join-fanout` | Medium | Inferred → Measured | A parent joined to two 1:N children in one query |
| `hot-query-summary` | Info | Derived | Always one: source, window covered, plans read |

**Plans, never executions.** On SQL Server the plan is the one Query Store or the plan cache stored
with the query, read only for the queries whose plan is analyzed. On PostgreSQL `pg_stat_statements` keeps the query normalized (`$1` for each value),
so the text is `PREPARE`d and explained with `EXPLAIN EXECUTE` under `plan_cache_mode =
force_generic_plan` — the plan for any value — in a read-only, rolled-back transaction, then
deallocated (PostgreSQL 12+). A query the planner cannot take (a `$n` of ambiguous type) loses its
plan, with a caveat. Plan filters are redacted like the text: a stored plan keeps its literals.

**Joins that multiply rows.** A query that joins one parent to two children through their foreign
keys returns, per parent, the product of its child rows — what EF Core writes for two `Include` on
collections. Recognized from the text and the keys (a chain `A ← B ← C`, a 1:1 child — a key, a
unique constraint or EF Core's unique index —, a single
child, one child joined through two keys, children of two different parent keys, or a join inside
`EXISTS`, `IN` or another `UNION` branch does not count; a parent paged in a derived table, EF Core's `Take` before `Include`, is not recognized), it is a signal. With `verifyJoins: true` one probe per shape counts, over the
whole tables, the rows the join returns against the children read separately, within the shared
[budget](safety.md#signal-verification); when the join returns more the title states both counts. No more is a fact at `Info` about the
total only: a few parents with many children of both kinds can hide in it.
The query's own filters are not applied: it is the shape of the data. `Metadata["ef"]` proposes
`.AsSplitQuery()`, or `UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)` on the context.

**Availability.** The result says which source was read and how many days it covers, and for each
source that was missing what would enable it: `pg_stat_statements` in `shared_preload_libraries`
and created in the database (a login outside `pg_read_all_settings` cannot see the first: the view is
read, and its error is the reason; any view the login is refused is reported the same way), Query Store on, `VIEW SERVER STATE` for the plan cache. With none an
empty list means nothing was measured. A SQL Server backup carries its Query Store; a PostgreSQL
dump does not carry `pg_stat_statements`, which is read on the server itself.

## `compare_ef_model`

Compares an EF Core model with the database: what production has that the code does not know, and the
reverse. The model is read from the migrations snapshot (`*ModelSnapshot.cs`) and the migration ids
from the `[Migration("…")]` attributes of the `*.Designer.cs` files beside it — **parsed as C#
text, never compiled**: no project is built and none of the user's code runs.

| Parameter | Default | Meaning |
|---|---|---|
| `snapshotPath` | required | Absolute path of the snapshot file, or of the folder holding exactly one |
| `schema` | — | Compare one schema only. Default: every schema the model maps |
| `historyTable` | `__EFMigrationsHistory` | The history table, when the DbContext sets `MigrationsHistoryTable` |
| `historySchema` | — | Its schema. Default: wherever a table of that name is found |

| Rule | Severity | Detects |
|---|---|---|
| `ef-migration-unknown` | High | A migration applied in the database that the code lacks: another branch, or a hotfix |
| `ef-table-missing` | High | A table the model maps that the database lacks |
| `ef-column-missing` | High | A column the model maps that its table lacks |
| `ef-index-missing` | High | A `HasIndex` with no index on those columns |
| `ef-migration-pending` | Medium / High | Migrations in the code not yet applied (one finding listing them); High when one is older than the last applied |
| `ef-index-unmodeled` | Medium | An index in the database the model does not declare — added by hand |
| `ef-fk-missing` | Medium | A relationship in the model with no foreign key in the database |
| `ef-index-uniqueness` | High / Low | A `HasIndex` whose index is unique on one side only: High when the model declares it unique and the database does not enforce it |
| `ef-column-type` | Medium | A column whose store type differs from the model's `HasColumnType` (length, precision, kind); spellings of one type (`int4` and `integer`, `datetime2` and `datetime2(7)`) are not differences |
| `ef-column-nullability` | Medium | A column nullable on one side and required on the other |
| `ef-unmapped-table` | Info | A table in a compared schema that no entity maps (the migrations history, temporal history tables and the tables of mappings not compared excluded) |
| `ef-drift-summary` | Info | Always exactly one |

Every finding is `Derived`. `Metadata["ef"]` names the `entity` and `property` to touch and, where it
can be written, the `fluent` call: for an unmodeled index
`b.HasIndex(e => e.CreatedAt).HasDatabaseName("ix_orders_created_manual")`. Declaring such an index
later makes the next migration contain a `CreateIndex` that fails where the index already exists;
the finding says to remove that call or make it conditional with `migrationBuilder.Sql`.

`Migrations` in the result gives the history table read, the last migration both sides know, the
pending ones, the `OutOfOrder` ones among them, the `Unknown` ones, the `OtherContexts` ones set
aside, and `ScriptCommands`: one `dotnet ef migrations script <from> <to> --context <Context>` per
run of consecutive pending migrations, whose output `check_migration_safety` reads. A pending
migration older than the last applied one — usually from a merge — is one `database update` applies
and a script from the last applied one leaves out, so it is flagged (High) and scripted on its own.

Every DbContext writes to the same history table by default. The migration ids of the other
contexts, read from the `*.Designer.cs` files under the folder above the snapshot's, are set aside
with a caveat rather than reported as unknown; a context whose migrations live elsewhere still shows
up as unknown, and the finding says so.

The history table is found by name in any schema (EF writes it unqualified, so it lands in the
login's default schema, not the model's; with one in each, the login's is read); `historyTable` and `historySchema` name another. None
found is a caveat, and the schema comparison still runs. Its rows' `MigrationId` is the one value
from a table the tool reads ([safety](safety.md#ef-cores-history-table)).

Indexes and foreign keys are matched by their columns, since a snapshot names them only when
`HasDatabaseName` does. Names are matched exactly first, then ignoring case; a name that matches
several objects by case alone is a caveat, never a guess, and nothing is reported missing or
unmapped for it.

**Compared:** plain entities, table-per-hierarchy subtypes (their columns nullable), owned types in
their owner's table, complex properties, alternate keys. **Not compared**, each named in a caveat:
table-per-type and table-per-concrete-type mappings, JSON columns (`ToJson`), table splitting, entity
splitting (`SplitToTable`; an owned type split leaves its owner's table uncompared too), views
(`ToView`) and keyless entities. Temporal tables are compared, their history table set aside. The same
type owned through two navigations is two entities. **Not considered here:** index filters, whose text EF and
the catalog write differently, and defaults. A project without migrations has no snapshot to read.

## `certify_query`

Saves a query the **user** confirmed as correct, under a name and the question it answers, so it can
be re-run by name, is cited in receipts and appears in the glossary.

| Parameter | Meaning |
|---|---|
| `name` | Short unique name; reusing it replaces the entry |
| `question` | The question it answers, in the user's words |
| `sql` | The confirmed `SELECT` |

The SQL is checked by the statement guard and planned with `EXPLAIN`; nothing is executed. Entries are
scoped to dialect and database and stored in one JSON file, `dblens/certified-queries.json` under the
local application-data folder (`%LOCALAPPDATA%` on Windows, `~/.local/share` on Linux,
`~/Library/Application Support` on macOS), overridable with `DbLens:CertifiedQueriesPath`. `list_certified_queries`
returns the entries for the current database.

