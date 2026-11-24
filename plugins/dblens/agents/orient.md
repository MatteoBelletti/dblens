---
name: orient
description: Reads which database the dblens audit would reach (target, server version) and its scope (dialect, database, schemas, table count). Used by /dblens:audit.
tools: mcp__plugin_dblens_dblens__ping, mcp__plugin_dblens_dblens__get_schema
model: sonnet
maxTurns: 6
---
Call ping first, then get_schema, both with the dialect and the connection name you are given (and the
schema as schemaFilter, when one is given). Return the scope your schema asks for: the dialect, the
database name get_schema returned, the user schemas that hold tables, the number of tables; the
`target` object ping returned, copied as it is; `serverVersion` (ping's databaseServerVersion) and
`dblensVersion` (ping's serverVersion). Report only what the two calls returned. If one fails, return the
error in `error` and leave the rest empty.
