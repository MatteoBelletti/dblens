---
name: setup
description: Gets a new user from the installed dblens plugin to a first audit report — checks the prerequisites, the connection and a duplicate registration, then runs the audit. Use when the user asks to set up dblens or run their first dblens audit.
---

# Set up dblens and run a first audit

Work through these steps in order and stop at the first one that fails, saying what to fix.

1. **.NET 10.** Run `dotnet --version`. The plugin starts the server with `dnx DbLens.Mcp`, which needs
   the .NET 10 SDK or later. Older or missing: point to https://dotnet.microsoft.com/download.
2. **Workflows.** The audit is a dynamic workflow. If the Workflow tool is not available in this
   session, say that dynamic workflows must be enabled (`/config`, "Dynamic workflows") and stop: run
   by hand, the audit loses its schema and its verifier.
3. **One registration.** Run `claude mcp list`. If a `dblens` server is listed besides this plugin's
   (`plugin:dblens:dblens`), say that the two are the same server and suggest keeping one. Remove
   nothing yourself.
4. **Connection.** The plugin reads the connection strings from its settings (`pg_connection`,
   `ss_connection`); empty means the local demo database of the dblens repository. Recommend a
   read-only login, and never ask the user to paste a connection string into the chat: they set it in
   the plugin's settings. Call the dblens `ping` tool, then `get_schema` for the dialect they use, and
   report the database name and the number of tables.
5. **First audit.** Ask which dialect and, optionally, which schema; whether there is an EF Core
   migrations snapshot to compare (an absolute path) and whether to include the personal-data scan.
   Then run the dblens `audit` workflow with args `{dialect, schema?, efSnapshot?, queriesFile?,
   privacy?, outDir, runLabel}` (no other key: the workflow refuses unknown ones): `outDir` an absolute folder in the current project (default `<project>/dblens-audit`),
   `runLabel` today's date as `YYYY-MM-DD`.
6. **Hand-off.** Give the report's path and its two-sentence summary. Say that confirmed findings
   were re-run by the verifier, that "Not re-checked" lists those it could not repeat, and that "To
   verify" and "Not covered" list what the audit could not establish.

dblens never writes to the database and returns no row data. The audit's verification runs read-only
aggregate counts in rolled-back transactions.
