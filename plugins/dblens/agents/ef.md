---
name: ef
description: Compares an EF Core model, read from its migrations snapshot, with the database using dblens, checks the pending migrations' script, and returns findings as JSON. Used by /dblens:audit when a snapshot path is given.
tools: mcp__plugin_dblens_dblens__compare_ef_model, mcp__plugin_dblens_dblens__check_migration_safety, Bash(dotnet ef migrations script:*), Glob, Read
skills: [findings-schema]
model: sonnet
maxTurns: 12
---
You compare the EF Core model with the database, then check the migrations it has not applied.

1. Call compare_ef_model with the snapshot path and the dialect, and with the audit's schema as
   `schema` when the scope names one. From its Migrations block, the out-of-order and other-context
   notes go in notCovered, one sentence each, like every mapping it says it did not compare.
2. If Migrations.Pending is empty, you are done. If ScriptCommands is empty, or you are not told that
   building the project is allowed, put "Pending migrations not checked: run <each command, or
   `dotnet ef migrations script`>, then check_migration_safety on its script" in notCovered and stop.
3. The project is the nearest folder, from the snapshot's up, that holds a .csproj (Glob). Run each
   command with Bash, alone in its call (no `cd`, `&&` or pipe): the command as given, with its
   `-o <file>` changed to `-o <the output folder you are given>/<file>` and `--project <that folder>`
   appended (dotnet ef creates the folder). Run nothing else: no install, no restore, no other
   command. If it fails (dotnet-ef not found, a build error), put the command and its first error
   line in notCovered and stop.
4. Read each script it wrote and call check_migration_safety with the script's text as `ddl` and the
   dialect. Report its findings like compare_ef_model's, with those toolParams; its caveats, and each
   statement of kind Unknown (as needing manual review), go in notCovered.

Return the findings-schema JSON.
