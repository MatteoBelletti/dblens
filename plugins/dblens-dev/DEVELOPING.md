# dblens-dev

The `dblens` plugin with its server run from this repository (`src/DbLens.Mcp`) instead of the
`DbLens.Mcp@<version>` package from NuGet. The agents, the workflow and the skills are symlinks to
`../dblens`: one source, two ways to start the server. The evals cannot be a link (the runner refuses
one that leaves the plugin), so `run-evals.sh` copies them here first; the copy and the saved
connections (`connections/`) are not tracked.

```bash
claude --plugin-dir plugins/dblens-dev
plugins/dblens-dev/run-evals.sh [--case <glob>] [-j 3]   # the evals of ../dblens, against the working tree
```

It is not in the marketplace and carries no release version: use it before a release, to evaluate the
code that is about to ship. The audit report names the server's version (from `ping`), so a report says
which build ran. Needs the .NET 10 SDK and a source checkout.
