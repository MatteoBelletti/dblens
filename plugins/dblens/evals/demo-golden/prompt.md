---
max_turns: 12
runs: 1
allowed_tools: [Skill, Workflow, Read, Write, ToolSearch, mcp__plugin_dblens_dblens__ping, mcp__plugin_dblens_dblens__get_schema, mcp__plugin_dblens_dblens__get_table_details, mcp__plugin_dblens_dblens__analyze_indexes, mcp__plugin_dblens_dblens__analyze_fragmentation, mcp__plugin_dblens_dblens__analyze_table_hygiene, mcp__plugin_dblens_dblens__analyze_denormalization, mcp__plugin_dblens_dblens__infer_relationships, mcp__plugin_dblens_dblens__check_cascade_paths, mcp__plugin_dblens_dblens__find_hot_queries, mcp__plugin_dblens_dblens__explain_query, mcp__plugin_dblens_dblens__compare_ef_model, mcp__plugin_dblens_dblens__scan_gdpr]
---

Run the dblens audit workflow with args {"dialect": "PostgreSql", "schema": "public", "privacy": true, "outDir": "./dblens-audit", "runLabel": "eval"}. When it finishes, Read the Markdown report it wrote and print it in full.
