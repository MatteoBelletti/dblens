# dblens-docs

Project documentation for a .NET solution in which **the code guarantees completeness** and a person or a
model writes only in bounded places. `dblens-docs` reads the solution as syntax (nothing is compiled or
run), writes a Markdown skeleton with a place for every project, DbContext, entity, endpoint, configuration
key, HTTP client and background service, and fails a build when the documents and the code disagree. It is
separate from the `DbLens.Mcp` server: it reads source code, a different permission
([safety](safety.md#dblensdocs-reads-source-code)).

```bash
dotnet tool install --global DbLens.Docs.Cli     # the command is dblens-docs
dblens-docs scaffold          # write docs/project/*.md from the code
dblens-docs check             # exit code 1 when the documents and the code disagree
```

## Commands

| Command | Does |
|---|---|
| `inventory [root] [--out FILE]` | The inventory as JSON: every item with a stable id, its source, its evidence, and what could not be read |
| `scaffold [root] [--docs DIR] [--language en\|it]` | Rewrites the generated regions; keeps the text you wrote outside them and the words in every slot |
| `check [root] [--docs DIR] [--strict] [--require-slots] [--json]` | Compares the documents with the code. Exit code 0, 1 (failed) or 2 (wrong command or folder) |
| `confirm <id> [root]` | A slot, as it reads now, becomes yours and current |
| `mcp [root]` | An MCP server (stdio) on that folder: `get_project_inventory`, `get_doc_slots`, `check_doc_coverage`, `set_doc_slot` |

Settings, if any, are in `dblens-docs.json` at the root: `{ "language": "it", "docs": "docs/project" }`.

## What the documents hold

Five files in `docs/project`: `architecture.md` (the project graph in Mermaid, and a table per project),
`data-model.md` (per DbContext and entity), `api.md`, `configuration.md` (keys, never values) and
`integrations.md` (HTTP clients and background services).

Each item is a **region**, marked by HTML comments that a rendered page does not show:

```markdown
<!-- dblens:item endpoint:GET /orders/{id} hash=9f2c1a07 gen=51d3b6e2 -->
### GET /orders/{id}

| Verb | Route | Handler | Declared as | File |
| --- | --- | --- | --- | --- |
| GET | /orders/{id} | GetOrder | minimal | src/Orders/Api.cs |
<!-- dblens:slot endpoint:GET /orders/{id} purpose max-lines=2 evidence=inferred hash=9f2c1a07 text=3a5e1f90 -->
Returns an order with its lines; 404 when it is not the caller's.
<!-- dblens:end -->
```

- The **table** is generated and rewritten at every run. `hash` is the item's hash when it was generated
  (what the code says, never a line number); `gen` is the hash of the generated text.
- The **slot** is the only place for words: a purpose, in at most `max-lines` lines. A configuration key has
  a place but no slot.
- Whatever you write **outside** the regions (an introduction, a note after a region) is kept.

### The three states of a slot

| `evidence` | Written by | At the next `scaffold` |
|---|---|---|
| `inferred` | a model | rewritten only if the item changed; if a person edited it, it becomes `human` |
| `human` | a person (by editing a model's words, or `confirm`) | never touched; if the item changed it becomes `stale` |
| `stale` | — | the item changed since the words were written: read it again, then `confirm` or rewrite |

## What `check` fails on

1. an item of the code with no region in the documents (`missing-item`);
2. a region whose item left the code (`orphan-item`);
3. an item that changed since its region was generated (`outdated-item`): run `scaffold`;
4. a generated region edited by hand (`generated-edited`);
5. a slot over its line limit (`slot-too-long`); a region twice (`duplicate-item`); a file with a broken
   marker (`malformed`).

An **empty** slot (`slot-empty`) and a **stale** one (`slot-stale`) are warnings, so the check is useful
before anyone writes a word; `--require-slots` and `--strict` make them errors.

## In CI

[`examples/dblens-docs/docs-check.yml`](../examples/dblens-docs/docs-check.yml) is a workflow that runs
`dblens-docs check` on a pull request. The author runs `dblens-docs scaffold` and commits the documents:
the diff of `docs/project` is part of the review.

## Words, written by a model

`dblens-docs mcp` lets a model read the slots that need words and write them, and nothing else
([what it may write](safety.md#dblensdocs-reads-source-code)). The `dblens-docs` plugin
([plugins/dblens-docs](../plugins/dblens-docs/README.md)) wraps it: `/dblens-docs:fill-slots` writes the slots with
one agent per batch, has a second agent read each sentence against the code, and rewrites once what the code
does not support. A person's slots are never touched.

## What it does not read

Messaging, conventional MVC routes (`MapDefaultControllerRoute`), a route built at run time and a group
passed through several layers are named in the inventory's `coverage`, not guessed.
See [roadmap/docs-m0.md](roadmap/docs-m0.md), [docs-m1.md](roadmap/docs-m1.md) and [docs-m2.md](roadmap/docs-m2.md).
