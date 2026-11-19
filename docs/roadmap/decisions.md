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
