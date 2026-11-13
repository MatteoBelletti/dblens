# Tool reference

Every tool dblens exposes, with its parameters, its rules and what it does **not** tell you.
For the terms used here (finding, signal, evidence, probe…) see the [glossary](../GLOSSARY.md).

All database tools share these parameters, so the tables below leave them out:

| Parameter | Type | Default | Meaning |
|---|---|---|---|
| `dialect` | `PostgreSql` \| `SqlServer` | `PostgreSql` | Target engine |
| `connectionString` | string | server default | Use a different database for this call only |

Scoped tools also take `schema` (null = every user schema) and `tables` (bare names, null = every
table in scope, at most 20).

| Tool | Reads | Executes SQL? | Results in |
|---|---|---|---|
| [`ping`](#ping) | nothing | no | — |
| [`get_schema`](#get_schema) | catalog | no | overview |
| [`get_table_details`](#get_table_details) | catalog | no | structure |
| [`analyze_indexes`](#analyze_indexes) | catalog + scan counters | no | Findings |
| [`analyze_fragmentation`](#analyze_fragmentation) | physical stats | no | Findings |
| [`check_cascade_paths`](#check_cascade_paths) | catalog | no | Findings |
| [`check_migration_safety`](#check_migration_safety) | catalog (table sizes) | **never** the script | Findings |
| [`explain_query`](#explain_query) | plan | only with `analyze: true`, pure `SELECT`, rolled back | Findings |
| [`scan_gdpr`](#scan_gdpr) | column names | no | Signals only |
| [`analyze_denormalization`](#analyze_denormalization) | catalog | only with `verifySignals: true`, counts only | Signals → Findings |
| [`infer_relationships`](#infer_relationships) | catalog, views, routines, query history | only with `verifyCandidates: true`, counts only | Signals → Findings |
| [`build_glossary`](#build_glossary) | as `infer_relationships`, plus comments | only with `verifyJoins: true`, counts only | a SKILL.md |
| [`query_with_receipt`](#query_with_receipt) | the data | **yes** — disabled unless the operator enables it | rows + receipt |
| [`certify_query`](#certify_query) / [`list_certified_queries`](#certify_query) | plan only | no | local file |

Caller SQL (`explain_query` with `analyze`, `query_with_receipt`) only runs on a read-only login; see
[safety.md](safety.md#read-only-login).

Every diagnostic result has the same skeleton: `Findings`, `Signals`, a `Summary` whose `BySeverity`
and `ByRule` dictionaries always contain every key, `Caveats`, and — where relevant — a `Disclaimer`.
Results are capped at 200 findings in total; facts are kept in preference to signals and a caveat
says when anything was cut.

---

## `ping`

No parameters. Returns the server version, a UTC timestamp and the supported dialects. Use it to
check the server is reachable.

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

## `get_table_details`

Columns (type, nullability, default), indexes with their definitions, foreign keys in both
directions, check and unique constraints, for 1–20 tables or views.

| Parameter | Default | Meaning |
|---|---|---|
| `tables` | required | `schema.name` or `name` |
| `defaultSchema` | `public` / `dbo` | Schema for unqualified names |

**Not included:** view bodies, row data, anything about names listed in `NotFound`.

## `analyze_indexes`

| Parameter | Default | Meaning |
|---|---|---|
| `includeUnusedIndexes` | `true` | Run `unused-index`, which reads scan statistics |

| Rule | Severity | Evidence | Detects |
|---|---|---|---|
| `fk-without-index` | High | Derived | FK columns with no index that leads with them |
| `duplicate-index` | Medium | Derived | Two indexes on the same columns with the same uniqueness |
| `redundant-index` | Low | Derived | A non-unique index that is a strict prefix of another |
| `unused-index` | Low | Measured | A non-unique index with zero scans |

Every finding has a `SqlFix` with the `CREATE INDEX` / `DROP INDEX` statement (kind `Ddl`), written
not to block writes: `CONCURRENTLY` on PostgreSQL, `ONLINE = ON` on SQL Server (Enterprise or Developer
edition; on Standard drop the option and schedule a window).
`unused-index` needs at least 7 days of uptime: scan counters reset on restart, so read the `Caveats` before dropping anything.

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

**Requirements:** PostgreSQL needs the `pgstattuple` extension and only btree indexes are measured;
SQL Server needs `VIEW DATABASE STATE`. When the statistics cannot be read the result says
`StatsAvailable: false` with an empty `Findings` list — that means *not measured*, not *healthy*.

**Not considered:** fill factor, page-split history, how fast an index re-fragments, whether anything
range-scans it. This is one input to a maintenance decision. For a maintenance strategy use
`sp_BlitzIndex` / Ola Hallengren's scripts (SQL Server) or PgHero / pganalyze (PostgreSQL).

## `check_cascade_paths`

| Parameter | Default | Meaning |
|---|---|---|
| `startingTable` | — | Qualified table, e.g. `public.users`. Omitted: every table targeted by an `ON DELETE CASCADE` key |
| `maxDepth` | `10` | Hard cap 50 |

| Rule | Severity | Detects |
|---|---|---|
| `cascade-circular-path` | Critical | A cycle in the cascade graph |
| `cascade-deep-path` | High | A chain deeper than 5 levels |
| `cascade-breaks-with-restrict` | High | A `NO ACTION` / `RESTRICT` key that makes the DELETE fail at runtime |
| `cascade-breaks-with-set-null` | Info | The chain turns into `SET NULL`: rows survive, detached |
| `cascade-self-referencing` | Info | A self-referencing `CASCADE` key (a tree) |
| `cascade-summary` | Info | Always exactly one: paths, max depth, tables reached |

Each path carries the approximate row count of every table it reaches. Only declared foreign keys are
analyzed: ORM cascades and triggers are invisible.

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
| `migration-destructive` | Critical | `DROP TABLE`, `DROP COLUMN` |
| `migration-summary` | Info | Always exactly one |

`ALTER TABLE … VALIDATE CONSTRAINT` (the second half of PostgreSQL's `NOT VALID` pattern) is recognized
and raises nothing: it scans without blocking reads or writes.

Severity is weighted by the real size of the table: the same statement is Critical on 50 million rows
and Medium on a lookup table (threshold: 1 million rows). The script is **never executed**.
Statements are matched by shape, not parsed: a statement of kind `Unknown` was *not recognized*,
which is not the same as safe.

## `explain_query`

| Parameter | Default | Meaning |
|---|---|---|
| `sql` | required | Exactly one statement |
| `analyze` | `false` | Execute it for real row counts and timings |

| Rule | Severity | Detects |
|---|---|---|
| `plan-seq-scan-large-table` | High / Medium | A full scan producing more than 10 000 rows |
| `plan-estimate-mismatch` | High | Estimated and actual rows differ by 10× or more (`analyze` only) |
| `plan-nested-loop-large-input` | Medium | A nested loop driven by more than 10 000 outer rows |
| `plan-sort-spilled-to-disk` | Medium | A sort or hash that did not fit in memory |
| `plan-scan-with-filter` | Low | A predicate applied after reading every row |

With `analyze: false` nothing runs and every row count is an optimizer estimate. With
`analyze: true` see the [safety model](safety.md#explain_query).

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

**Everything is a signal**: `Findings` is always empty. No row is read, so an empty result does not
mean there is no personal data. The `Coverage` block counts the columns whose names carry no usable
signal (`c1`, `fld_07`, codenames) and lists examples; always report it with the results.

## `analyze_denormalization`

| Parameter | Default | Meaning |
|---|---|---|
| `verifySignals` | `false` | Measure the checkable signals against the data |

| Rule | Severity | Confidence | Detects | Verifiable |
|---|---|---|---|---|
| `denorm-repeating-group` | Medium (3+) / Low | — | `phone1`, `phone2`, `phone3` | no |
| `denorm-derived-column` | Medium | 0.6 | A cached child count (`order_count`) | **yes** |
| `denorm-derived-column` | Low | 0.7 | A concatenation of siblings (`full_name`) | **yes** |
| `denorm-duplicated-column` | Low | 0.55 | The same column on two FK-linked tables | **yes** |
| `denorm-multivalue-column` | Medium | 0.5 | Text named like a list (`tags`, `role_list`) | no |
| `denorm-wide-table` | Low / Info | 0.5 / 0.3 | More than 40 columns | no |

Sums (`total`, `amount`) are never verified: the catalog does not say which column they sum.

With `verifySignals: true`, see [signal verification](safety.md#signal-verification). A measured
signal moves into `Findings` with `Evidence: Measured`; its title states the count and its
interpretation goes under `WHAT THIS MAY MEAN:`. A refuted signal is still reported, as `Info`.
`Verification` in the result is `null` when verification was not requested — different from
verification finding nothing.

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

A candidate with more than half orphans is dropped from the map and reported as an `Info` finding.
Candidates come from `relationship-undeclared-fk`, strongest evidence first:

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
| `verifyJoins` | `false` | Count orphans for inferred joins, as `infer_relationships` does |
| `writeSkillTo` | — | Absolute directory to write `SKILL.md` into, e.g. `<project>/.claude/skills/erp-data-map` |
| `overwrite` | `false` | Replace an existing `SKILL.md` |

For every table: approximate rows, how many views / routines / logged statements reference it (and
how often they ran), its documented meaning, its columns (type, key, meaning), its join paths labelled
`declared`, `inferred (basis)`, `verified` or `verified with orphans`, and candidate measures (numeric
columns named like amounts or totals). Certified queries come first.

**Meanings come only from schema comments** (`COMMENT ON` / `MS_Description`). An undocumented column
is shown as `—`; the skill tells the model to ask rather than guess. A caveat says when nothing is
documented. No row values are included.

## `query_with_receipt`

**Disabled unless the server runs with `DbLens:AllowQueryResults=true`** — it is the one tool that
returns values from the data. See [safety.md](safety.md#query-results).

| Parameter | Default | Meaning |
|---|---|---|
| `sql` | — | One `SELECT` |
| `certified` | — | Run a certified query by name instead |
| `crossCheckSql` | — | The same figure computed another way |
| `tolerance` | `0` | Relative difference allowed between numbers in the cross-check |

Returns at most 100 rows and a **receipt**:

| Receipt field | Content |
|---|---|
| `Sql` | exactly what ran |
| `TablesTouched` | the relations in the plan (views expanded to base tables) |
| `Database`, `ExecutedAt` | where and when |
| `CrossCheck` | `Agree`, `Disagree` (with both values) or `NotComparable`, and the second result |
| `CertifiedAs` | set when the SQL matches a certified query |

A cross-check compares the two results as sets of rows, so row order does not matter and `120.50`
equals `120.5`. Without one, a caveat says the figure is unconfirmed; with a disagreement, the caveat
says to report both figures rather than pick one. Useful pairs: two join paths to the same total, or
line items summed against the stored header total.

## `certify_query`

Saves a query the **user** confirmed as correct, under a name and the question it answers, so it can
be re-run by name, is cited in receipts and appears in the glossary.

| Parameter | Meaning |
|---|---|
| `name` | Short unique name; reusing it replaces the entry |
| `question` | The question it answers, in the user's words |
| `sql` | The confirmed `SELECT` |

The SQL is checked by the statement guard and planned with `EXPLAIN`; nothing is executed. Entries are
scoped to dialect and database and stored in one JSON file — `%LOCALAPPDATA%/dblens/certified-queries.json`
(or `~/.local/share/dblens/…`), overridable with `DbLens:CertifiedQueriesPath`. `list_certified_queries`
returns the entries for the current database.

