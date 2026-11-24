# dblens-docs plugin

Writes the words of the project documents that `dblens-docs` generates ([guide](../../docs/dblens-docs.md)).
It writes only slots, through the `docs` MCP server, never a document with Write or Edit.

```bash
dotnet tool install --global DbLens.Docs.Cli   # the plugin starts `dblens-docs mcp`
dblens-docs scaffold                           # the skeleton, in the folder you work in
```

Then `/dblens-docs:fill-slots` in that folder. It lists the slots that are empty or stale, runs the
`fill-slots` workflow (a writer agent per batch of at most eight slots; a reviewer agent that reads each
written slot against the source; one rewrite of what is not supported) and runs the check with every slot
required.

- `skills/slot-rules`: what a purpose says and may rest on.
- `agents/writer`, `agents/reviewer`: what each may call (the MCP tools, Read, Grep, Glob).
- `evals/fill`: a small API with one slot already a person's; run with `run-evals.sh` (the working tree's
  build; at most $2).

It is not in the marketplace: the package `DbLens.Docs.Cli` is not published, so there is no version to pin.
