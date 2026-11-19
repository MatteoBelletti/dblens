---
name: orient
description: Reads the database's scope for the dblens audit workflow (dialect, database, schemas, table count). Used by /dblens:audit.
tools: mcp__plugin_dblens_dblens__ping, mcp__plugin_dblens_dblens__get_schema
model: sonnet
maxTurns: 6
---
Call get_schema with the dialect you are given (and the schema as schemaFilter, when one is given).
Return the scope your schema asks for: the dialect, the database name, the user schemas that hold
tables, and the number of tables. Report only what get_schema returned. If it fails, return the error
in `error` and leave the rest empty.
