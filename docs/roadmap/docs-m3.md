# DbLens.Docs — M3: words written by a model

Plan: [DbLens.Docs — Piano](https://claude.ai/artifact/Q6e44ur3ADHDfb4LHeSQ9k). Exit criterion of the plan: the
words are written only in the slots, the check passes, and what the code does not support is caught.

## What was built

- `SlotWriter` (pure): lists the slots with the facts of their item and who wrote what is there; writes one
  slot, refusing a person's words, an outdated document, too many lines, long lines, blank lines and HTML
  comments.
- `DbLens.Docs.Mcp`: the four tools (`get_project_inventory`, `get_doc_slots`, `check_doc_coverage`,
  `set_doc_slot`) and a `DocsWorkspace` fixed at start-up. Hosted by `dblens-docs mcp`.
- `plugins/dblens-docs`: `slot-rules`, agents `writer` and `reviewer`, the `fill-slots` workflow and skill,
  and the `fill` eval.

## Decisions and deviations

- **The server is a library of tools hosted by the CLI** (`dblens-docs mcp`), not a second executable: one
  tool to install, and the plugin's `.mcp.json` is one line.
- **No tool takes a path.** The folder is the one the server started in; a model cannot point it elsewhere.
- **There is no way for a model to clear a slot.** A sentence the reviewer rejects is rewritten once and the
  result reported as unreviewed; removing it is for a person.
- **The reviewer is a second agent, not a rule.** Whether words are supported by code is a reading task;
  the structural limits (lines, markers, protected slots) are enforced in code.
- **Not in the marketplace, no pin**: the package is not published (see the plan: no publishing without a
  decision).

## Evals

`evals/fill`, run twice of the three allowed: the first run's three factual graders failed to compile (the
regex engine takes no `(?i)`); the second scored 1.00 ($0.18, 40 s). Words written for the small API were read
by hand: each sentence is in the code (including that `DELETE /orders/{id}` does not check the customer, which
no table says). One run proves the mechanics, not the quality: nothing here measures how often a reviewer
passes a wrong sentence.

## Review

One round (ten findings, all fixed, each with a test): words a person edited but no scaffold had marked yet
are protected (`SlotWriter.IsPersons`); `scaffold` keeps an edited slot stale when the item also changed; `check`
sees generated text the item hash does not cover (a moved file, the project graph) and a region without its slot;
a note after a region whose item left is kept at the end of the file; concurrent `set_doc_slot` calls are
serialised; links in the docs path are refused; an invalid `dblens-docs.json` is exit code 2; ids with `%` survive
escaping; `kind` takes names only.
