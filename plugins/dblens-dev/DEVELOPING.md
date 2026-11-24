# dblens-dev

The `dblens` plugin with its server run from this repository (`src/DbLens.Mcp`) instead of the
`DbLens.Mcp@<version>` package from NuGet. The agents, the workflow and the skills are symlinks to
`../dblens`: one source, two ways to start the server.

```bash
claude --plugin-dir plugins/dblens-dev
claude plugin eval plugins/dblens-dev --mocks off …   # the evals of ../dblens run against the working tree
```

It is not in the marketplace and carries no release version: use it before a release, to evaluate the
code that is about to ship. The audit report names the server's version (from `ping`), so a report says
which build ran. Needs the .NET 10 SDK and a source checkout.
