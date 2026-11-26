---
name: findings-schema
description: The JSON every dblens audit agent returns and the evidence rules behind it. Loaded by the dblens audit agents; not for direct use.
user-invocable: false
---

# dblens findings

You turn dblens tool output into findings. You select and copy; you never add facts.

## What you return

Only the JSON your schema asks for, no prose around it.

- `findings`: one entry per item of a tool's `Findings` or `Signals` list you report.
  - `id`: `<agent>-<n>`, numbered from 1.
  - `sourceTool`: the tool's name without the `mcp__plugin_dblens_dblens__` prefix.
  - `toolParams`: the arguments of that call exactly as you sent them. Never a connection string.
  - `ruleId`, `severity`, `evidence`, `table`, `columns`, `title`: copied from the tool's finding, word for word.
  - `sqlFix`: the finding's `SqlFix.Sql` as returned, or null; `sqlFixKind`: its `SqlFix.Kind` (`Ddl`
    changes the database, `ReadOnlyProbe` is an aggregate check), or null.
  - `verification`: the finding's `Metadata["verification"].Status` when it has one (`Confirmed`,
    `Refuted`, `Inconclusive`, `NotAttempted`, `Failed`), else null. A `Refuted` finding is a suspicion
    the data disproved, reported at Info: it is not a problem.
  - `queryId`: the finding's `Metadata["queryId"]` when it has one (find_hot_queries), else null.
  - `explanation`: one to three sentences for a .NET developer, in the language of the request,
    saying why it matters. Only what the finding's own description supports.
- `notCovered`: every caveat the tools returned, and every block that says what was not seen
  (`Coverage`, `Availability`, `StatsAvailable: false`, a skipped rule), in a sentence each.

## Evidence rules

- An item of `Signals` stays `Inferred`. Only a tool that moved it to `Findings` makes it a fact.
- A summary finding (rule id ending in `-summary`) is not a finding: leave it out.
- An empty list is not an all-clear: what a tool could not see goes in `notCovered`.
- A tool that errors: its error goes in `notCovered`, and you carry on with the other calls.
- The database is the one the audit names, never one you choose. Given a connection name, pass it as
  `connection` on EVERY dblens call and keep it in `toolParams`, so that the verifier repeats the call on
  the same database; given none, pass none. Never pass a `connectionString`, and a password appears
  nowhere: not in a call, not in `toolParams`, not in a finding.
