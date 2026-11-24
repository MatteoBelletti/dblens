# DbLens.Docs — M2: skeleton and check

Plan: [DbLens.Docs — Piano](https://claude.ai/artifact/Q6e44ur3ADHDfb4LHeSQ9k). Exit criterion: the check fails
on a missing item, a vanished one, and a region touched by hand. Guide: [../dblens-docs.md](../dblens-docs.md).

## What was built

- `DbLens.Docs.Documents`: `DocumentModel` (markers, parse and render, inverse of each other),
  `ItemHash`, `FactsRenderer` (the tables, in English and Italian), `ProjectGraph` (Mermaid),
  `DocPlan` (which item goes in which file and in which order), `Scaffolder`, `DocumentCheck`, `DocsFolder`
  (the only disk access, refusing a docs folder that leaves the root).
- `src/DbLens.Docs.Cli`: the `dblens-docs` tool, with `inventory`, `scaffold`, `check`, `confirm`.
- `examples/dblens-docs/docs-check.yml`: a GitHub Action.

## Decisions and deviations from the plan

- **Two more attributes in the markers.** `gen=` (the hash of the generated text, to tell a hand edit from
  a regeneration) and `text=` (the hash of a slot's text as the tool wrote it, to tell that a person
  edited a model's words). `was=` records what a stale slot was.
- **A DbContext is an item** (`context:<Name>`), so it has its place and the slot of up to 4 lines the plan
  asks for. The inventory's schema gains `ItemKind.Context`.
- **A configuration key has a place, not a slot**: forty `Logging:LogLevel:*` slots would be noise.
- **Empty and stale slots are warnings, not errors.** M2 is a release without AI: failing a build because
  nobody has written yet would make it useless. `--require-slots` and `--strict` raise them.
- **The generated text holds no line number**, so an unrelated edit above a declaration changes neither the
  document nor the slot's state.
- **Vanished items are removed by `scaffold`**, and the report says which of them held words (the diff
  shows them too). It never rewrites a file whose markers are broken.
- **Package id `DbLens.Docs.Cli`**, command `dblens-docs`: the plan's `DbLens.Docs` is the library
  project's name, and NuGet refuses two projects of one name in a solution.
- **`confirm` is a command the plan does not list**: a stale human slot needs a way to become current
  without rewriting its words.

## Tests

`DocumentModelTests` (round trip, ids with `--`, CRLF, broken markers), `ScaffoldTests` (first run, rerun
changes nothing, text outside the regions kept, state changes of a slot, `confirm`, language, folders that
leave the root), `DocumentCheckTests` (every rule, and no documents at all), `DocsCliTests` (the loop
scaffold, change, check fails, scaffold, check passes; exit codes; settings).
