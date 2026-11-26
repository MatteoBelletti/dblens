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
   `ss_connection`); empty means no configured database, and the plugin never falls back to the demo. Recommend
   a read-only login, and never ask the user to paste a connection string into the chat: they set it in
   the plugin's settings. Call the dblens `ping` tool for the dialect they use (with a saved connection
   name if they use one): it connects and returns `target` (database, host, port, source, login) and the
   server's version. If `target.source` is `DemoDefault`, or the connection is the dblens demo, say so and ask
   whether the demo is what they want to check. If the login can write (an administrator such as `postgres`
   or `sa`) and the server is local, ask first whether it is a throwaway copy: a tunnel to production
   (`ssh -L`, `kubectl port-forward`, a cloud SQL proxy) looks local too, and then they need a read-only login.
5. **First audit.** Hand over to the `audit` skill (`/dblens:audit`): it asks which database, shows what it
   will analyze and waits for a yes before it starts. Do not start the workflow yourself.
6. **Hand-off.** The audit skill gives the report's path and its two-sentence summary. Say that
   confirmed findings were re-run by the verifier, that "Not re-checked" lists those it could not repeat,
   and that "To verify" and "Not covered" list what the audit could not establish.

dblens never writes to the database and returns no row data. The audit's verification runs read-only
aggregate counts in rolled-back transactions.
