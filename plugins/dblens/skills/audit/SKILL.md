---
name: audit
description: Audits a PostgreSQL or SQL Server database with dblens. First asks which database, shows what it will analyze and waits for a yes, then runs the audit workflow (agents in parallel, a verifier that re-runs every finding, a report with the evidence). Use when the user asks for a dblens audit, health check or review of a database.
argument-hint: "[connection=<name> expectedDatabase=<db> dialect=<PostgreSql|SqlServer> schema=… efSnapshot=… efScript=… queriesFile=… privacy=… outDir=…]"
---

# Audit a database

dblens never picks the database: you and the user do, here, before the workflow starts (a workflow
cannot ask a question halfway). Follow the steps in order.

## 1. Which database

If the request already carries `connection=<name>`, `dialect=` and `expectedDatabase=`, the user named
the database: skip to step 3. Otherwise ask with AskUserQuestion, one question, the options being what
you can actually find:

- **Local containers.** Run `docker ps --format '{{.Names}}\t{{.Image}}\t{{.Ports}}'` and offer each
  PostgreSQL or SQL Server container with its name, image and published port. If Docker is not
  running or has none, say so and offer the other options only.
- **The database the plugin is configured with**, when `pg_connection` or `ss_connection` is set in the
  plugin's settings. Do not read the settings: step 3's `ping` shows what it reaches.
- **The dblens demo database**, only if the user picks it by name. Never offer it as a default.

Always allow "Other", for a remote database: tell the user to put its connection string in the plugin's
settings (`/plugin`, dblens, Configure), never in the chat, and start again.

## 2. A container: save a connection without printing its password

For a container, save a named connection with the helper, which reads the container's own settings and
writes a file only the user can read. It prints no password, and you must not print one either: do not
run `docker inspect` or `docker exec … env` yourself.

```bash
bash "${CLAUDE_SKILL_DIR}/connection.sh" container <container> <name> [database]
```

`<name>` is `audit-<container>`; SQL Server needs the database name (ask for it). For the demo:
`bash "${CLAUDE_SKILL_DIR}/connection.sh" demo <PostgreSql|SqlServer> demo`.

**A login that can write.** A container's own login (`postgres`, `sa`) is an administrator. The audit
reads only the catalog and runs aggregate counts in rolled-back transactions, but say it, and ask whether
this is a throwaway copy. If it is a tunnel or a copy of production (`ssh -L`, `kubectl port-forward`,
a cloud SQL proxy all look local), stop and ask the user for a read-only login in the plugin's settings.

## 3. Say where it will go, and wait

Call the dblens `ping` tool with the `dialect` and, for a saved connection, `connection` (for the
configured database, neither a connection nor a connection string). It connects for real and returns
`target` and `databaseServerVersion`; an error means nothing was chosen, so report it and stop. Then call
`get_schema` the same way, with `schemaFilter` if the user wants one schema, for the number of tables.

Show: "I will analyze **<database>** on **<host>:<port>** (<source>, as <user>), <server version>,
<n> tables." If `source` is `DemoDefault`, or the saved connection is the demo, say plainly that this is the
dblens demo database and ask whether that is what they want. Ask for a yes; start only on it.

## 4. Run it

Ask whether to include `efSnapshot` (an absolute path to an EF Core migrations snapshot or its folder),
`efScript` (allow `dotnet ef migrations script` to build their project, only with a snapshot), `privacy`
(scan column names for personal data) and `queriesFile`, unless the request already says. Then call the
Workflow tool with `dblens:run-audit` and args:

`{dialect, expectedDatabase, connection?, schema?, efSnapshot?, efScript?, queriesFile?, privacy?, outDir, runLabel}`

`expectedDatabase` is the database name you showed in step 3 (`target.database`). `connection` only the
name, never a connection string. `outDir` an absolute folder in the current project (default
`<project>/dblens-audit`), `runLabel` today's date as `YYYY-MM-DD`. No other key: the workflow refuses it.
A report is never overwritten: a second run on the same day writes `-2`.

## 5. Hand-off

Give the report's path and its two-sentence summary. Say which database the report names (its first
lines), that confirmed findings were re-run by the verifier, and that "Not re-checked", "To verify" and
"Not covered" list what the audit could not establish. If the workflow stopped because the server
reached another database than the one confirmed, say so: nothing was audited.

dblens never writes to the database and returns no row data. The audit's verification runs aggregate
counts in rolled-back transactions.
