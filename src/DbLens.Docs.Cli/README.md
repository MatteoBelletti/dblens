# DbLens.Docs.Cli

`dblens-docs` keeps the documentation of a .NET solution complete: it reads the solution as syntax (nothing is
compiled or run), writes a Markdown skeleton with a place for every project, DbContext, entity, endpoint,
configuration key, HTTP client and background service, and fails a build when the documents and the code disagree.
The words go in slots a person or a model fills; the facts are generated.

```bash
dotnet tool install --global DbLens.Docs.Cli
dblens-docs scaffold      # docs/project/*.md
dblens-docs check         # exit code 1 when the documents and the code disagree
dblens-docs mcp           # an MCP server on the folder, to let a model write the slots
```

Guide: https://github.com/MatteoBelletti/dblens/blob/master/docs/dblens-docs.md
