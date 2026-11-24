# Multi-agent roadmap: decisions and Phase 0 results

The multi-agent plan adds an optional Claude Code plugin on top of the MCP server: a repeatable
`/dblens:audit` workflow whose agents each use a few dblens tools, an adversarial verifier that
re-runs them, and a report with the evidence. The server stays usable exactly as before, without the
plugin. This page records what was decided and what the Phase 0 spikes established, on 2026-11-19,
against dblens 0.4.0 and Claude Code 2.1.328.

## Decisions

| # | Question | Decision |
|---|---|---|
| D1 | How the audit learns which EF migrations are applied | Through `compare_ef_model`, which already reads the history table's migration ids ([safety](../safety.md#ef-cores-history-table)). No new path to row data. |
| D2 | Where workload queries come from | `find_hot_queries` (statistics the engine keeps, redacted); a file of queries only for a dump that carries no statistics. |
| D3 | Hypothetical indexes (HypoPG) | Out of scope: an extension to install and a function call for `StatementGuard` to admit. |
| D4 | A `compare_schemas` tool (two databases) | Deferred: the EF flow no longer needs it. Reopened if staging must be compared with production. |
| D5 | EF type, length, nullability, uniqueness and filter drift | Phase 2, as rules of `compare_ef_model`: the snapshot reader already parses those fields. |
| D6 | How the plugin starts the server | `dnx DbLens.Mcp@<version> --yes` (.NET 10, nothing installed): spike S1. |
| D7 | Language of the audit report | The language of the session that runs it. Code and repository docs stay in English. |

## Phase 0 spikes

| # | Question | Result |
|---|---|---|
| S0 | Can the plugin spikes run in a cloud session? | Yes: `claude -p … --plugin-dir <dir>` runs non-interactively; `claude plugin validate` and `claude plugin eval` exist. |
| S1 | Does the server start from the plugin? | Yes, with `.mcp.json` `{"command": "dnx", "args": ["DbLens.Mcp@0.4.0", "--yes"]}`. A `get_schema` call reached the demo database (12 tables in `public`). |
| S2 | Tool names | `mcp__plugin_dblens_dblens__<tool>` for all 18 tools (plugin `dblens`, server `dblens`). MCP tools are deferred: an agent finds them through tool search unless its `tools:` lists them. |
| S3 | Can a workflow's `agent()` use a plugin agent? | Yes: `agent(prompt, {agentType: 'dblens:<agent>', schema})`. |
| S4 | Does `tools:` restrict a plugin agent? | Yes: an agent listing two dblens tools got "No such tool available" for a third, although the session allowed it. Tools are listed by full name. |
| S5 | Does a `schema` output hold about 50 findings? | Yes: a 55-item array came back validated. |
| S6 | Tool surface snapshot | `ToolSurfaceSnapshotTests` against `tests/DbLens.Tests/Fixtures/tool-surface.json`: a tool or parameter removed, retyped, given another default or newly required fails; an addition is recorded with `DBLENS_UPDATE_TOOL_SURFACE=1`. |

## Corrections to the plan

- The workflow reference names no `small` size tied to the Pro plan: what it documents is a concurrency
  cap of `min(16, CPUs − 2)` agents per workflow. The Phase 1 criterion becomes a measured one: the
  demo audit's token cost, recorded in this file.
- A workflow runs only when the user asks for it explicitly; `/dblens:audit` is that ask.
- `Date.now()`, `Math.random()` and an argless `new Date()` throw in a workflow script: the run label and
  output folder arrive through `args`.
- The plugin directory wants a square PNG icon (`.claude-plugin/icon.png`, 512–2048 px); validation
  only warns without it.

## Phase 1 measurements

The plugin lives in `plugins/dblens` and the marketplace at the repository root. Its eval suite
(`plugins/dblens/evals`) runs the real server against the demo databases:

| Case | What must hold | Result | Cost |
|---|---|---|---|
| `demo-golden` (PostgreSQL `public`, with `privacy`) | unindexed FKs, a cascade blocked by NO ACTION, the undeclared `shipments.ship_to`, a cached aggregate; PII only as signals | pass, 9 of 9 graders | $1.06, 4 min |
| `demo-good` (PostgreSQL `demo_good`) | `Confirmed findings: 0`, every agent ran and every finding re-checked | pass | $0.18, 40 s |

Run them with `claude plugin eval plugins/dblens --mocks off --runs 1 --allow-tools Workflow Write <the
dblens tools>`: `Workflow` must be granted, or the model runs the phases by hand and loses the schema
and the verifier. That is also why `/dblens:setup` stops when dynamic workflows are off.

The demo has no duplicate indexes, although `docs/architecture.md` listed them: the audit found that,
and the documentation was corrected rather than the demo.

## Phase 2: new rules (0.6.0)

| Rule | Tool | Evidence | Notes |
|---|---|---|---|
| `fk-type-mismatch` | `analyze_indexes` | Derived | Compared with the referenced key, read even when it is outside the scope. PostgreSQL only: SQL Server refuses such a key (Msg 1753, 1778), which its edge tests assert. |
| `plan-implicit-conversion` | `explain_query`, `find_hot_queries` | Derived on SQL Server, Inferred on PostgreSQL | SQL Server names the conversion (`PlanAffectingConvert`, "Seek Plan"); PostgreSQL shows only a cast in a sequential scan's filter, which may be the caller's own. |
| `ef-column-type`, `ef-column-nullability`, `ef-index-uniqueness` | `compare_ef_model` | Derived | D5. Index filters and defaults are not compared: EF and the catalog write them differently. A property the model computes is skipped. |

Spellings of one store type are normalised before comparing (`int4` and `integer`, `datetime2` and
`datetime2(7)`, `timestamptz(3)` and `timestamp(3) with time zone`, `rowversion` and `timestamp`), so
the real `dotnet ef` schemas of the EF fixtures report no drift on either engine.

## Phase 3: the `ef` agent (unreleased)

The EF golden set is the demo's `shop` schema (`db/*/04-shop.sql`): the first migration of
`samples/DemoShop.EfCore`, then the drift the EF tests expect, a hotfix row in
`shop.__EFMigrationsHistory`, and two pending migrations, the second a `DROP COLUMN`. The sample keeps
its EF Core 10 migrations, and the repository root pins `dotnet-ef` in `.config/dotnet-tools.json`.

| Step | Who | Gate |
|---|---|---|
| `compare_ef_model` | `ef` agent | always, with `efSnapshot` |
| `dotnet ef migrations script <ScriptCommands>` | `ef` agent, Bash limited to that command | `efScript: true`, asked of the user first; otherwise the command goes in "Not covered" |
| `check_migration_safety` on the script | `ef` agent, re-run by the verifier | a script was written |

`check_migration_safety` read EF's own bookkeeping as `Unknown` statements: the history row, and on
SQL Server the lookup of a column's default constraint. It now skips the first and classifies the
`EXEC` that drops the constraint as the `DROP CONSTRAINT` it runs, so a clean EF script has no
`Unknown` left.

The `ef-golden` eval case runs the audit on `shop` with a scaffold that copies and builds the sample.
In this repository's cloud container the eval harness runs as root and its sandbox cannot write the
`TMPDIR` it gives Bash, so MSBuild fails there and the pending-migration step lands in "Not covered":
8 of 9 graders pass, $0.63. The same audit run with `claude -p --plugin-dir` (no eval sandbox)
scripted both pending migrations and reported 15 confirmed findings, none unverified, the first a
Critical `migration-destructive` on `shop.users.date_of_birth`. That run found a bug the tests had not:
an index on `shop.payments`, which the script itself creates, was weighed against the demo's
`public.payments`. A qualified name missing from the catalog no longer falls back to a table of the
same name in another schema.

## Phase 4: distribution (unreleased)

The marketplace (`.claude-plugin/marketplace.json`, source `./plugins/dblens`) and the README's
"Claude Code plugin (optional)" section came with Phase 1. Instead of writing the tag's version into
`plugin.json`, `release.yml` refuses a tag whose version the plugin, its `DbLens.Mcp@` pin and
`server.json` do not already carry (`ReleaseVersionTests` checks the same in CI): master never pins
a package NuGet does not list.

Smoke test on 2026-11-20, each in an empty Claude Code configuration: `claude plugin install dblens
--marketplace <repository>` installs it and `claude mcp list` shows `plugin:dblens:dblens` connected;
`claude mcp add dblens -- dnx DbLens.Mcp@0.6.0 --yes`, without the plugin, connects too, and
`get_schema` and `analyze_indexes` on `public` return the demo's 12 tables and 9 `fk-without-index`.
The `.mcpb` in Claude Desktop is not covered: there is no desktop in the cloud container.

## Silent targets (0.7.0, not released)

The server or the plugin worked on a database, an engine or a version other than the one the user
meant, without saying so. Decisions of 2026-11-21, each with a test:

| Point | Decision |
|---|---|
| Demo fallback | `DbLens:DemoFallback`, default **false**: no connection string for a dialect is an error naming the setting. `appsettings.Development.json`, the Makefile and the tests turn it on. [D] |
| Default dialect | The one configured dialect; two configured and no `dialect`, or a `connectionString` without `dialect`, is an error. The schema keeps `dialect = PostgreSql` (contract); the call filter records which arguments were sent. |
| Target | `target` (dialect, host, port, database, login, `isLocal`, `source`) in every answer, from the driver's connection string builder, never the password. `ping` connects and returns it with the server version. |
| Passwords | The audit passes a connection **name** (`connection`), never a connection string: a file `<name>.conn` in `DbLens:ConnectionsPath` that the audit skill writes from a container's own environment (mode 0600). The plan's `args.connectionString` would have put the password in every agent prompt. [D] |
| Localhost | A caveat, not a new key: the existing `DbLens:ReadOnlyLoginOnLocalhost=true` is the switch. [D] |
| Development server | `plugins/dblens-dev`: same name, agents, workflow and skills (symlinks), the server run from the working tree; `run-evals.sh` copies the evals in, because the runner refuses an evals link that leaves the plugin. [D] |
| Certified queries | Keyed by dialect, server (`host:port`) and database; old entries without a server still apply and carry a caveat. |
| Reports | Never overwritten: `-2`, `-3`. |

The workflow `audit` is now `run-audit` and requires `expectedDatabase`; `/dblens:audit` is the skill
that asks and confirms.

Evals against the working tree (`plugins/dblens-dev/run-evals.sh`, 2026-11-24): `demo-golden` 10 of 10
($1.00) and `demo-good` 4 of 4 ($0.31) on a named connection; `target` 4 of 4 ($0.25): a second PostgreSQL
(`target_db`, a copy of `demo_good`) is named by the report and the report never mentions `dblens_demo`;
`no-configuration` 3 of 3: the skill asks and starts nothing. Two cases failed their first runs on the
evals, not on the product: the development server was still starting when the first agent called `ping`
(it now starts from the built DLL, and an eval pings first, as the skill does), and graders that read the
model's closing message now read the report file. `wrong-database` and `ef-golden` were last run
with those graders unfixed (the first, 6 turns used up on waiting for the server; the second, a summary
printed instead of the report), so their graders are untested against the final cases; the check
itself, that the workflow refuses a server that reaches another database than `expectedDatabase`, is
exercised by running the script with a stubbed `agent()`. The pending-migration step of `ef-golden`
still lands in "Not covered" inside the eval sandbox of a root container.

The plugin now needs a server that has `connection`, `DbLens:DemoFallback` and the connecting `ping`:
0.7.0. Merging this to `master` without releasing 0.7.0 would leave the marketplace plugin pinned to a
package that lacks them, so the two go together.
