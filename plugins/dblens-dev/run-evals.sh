#!/usr/bin/env bash
# Runs the evals of ../dblens against the server in this working tree.
#
#   plugins/dblens-dev/run-evals.sh [claude plugin eval options…]
#
# The eval runner refuses an evals folder that is a link to outside the plugin, so the cases are
# copied here first (the copy is not tracked). The options it adds are the ones the suite needs:
# the real server, its scaffolds, and the tools the audit uses.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# Built once here: serve.sh would otherwise compile while the first agent is already calling ping.
dotnet build "$here/../../src/DbLens.Mcp" -nologo -v q -c Debug

rm -rf "$here/evals"
mkdir -p "$here/evals"
(cd "$here/../dblens/evals" && tar --exclude=./results -cf - .) | (cd "$here/evals" && tar -xf -)

tools=(ping get_schema get_table_details analyze_indexes analyze_fragmentation analyze_table_hygiene
  analyze_denormalization infer_relationships check_cascade_paths find_hot_queries explain_query
  compare_ef_model check_migration_safety scan_gdpr)
allow=(Workflow Write Glob AskUserQuestion "Bash(docker ps:*)" "Bash(dotnet ef migrations script:*)")
for t in "${tools[@]}"; do allow+=("mcp__plugin_dblens_dblens__$t"); done

cd "$here/.."
exec claude plugin eval dblens-dev --runs 1 --scaffold --trust-plugin --ablation none --no-publish --mocks off \
  --max-cost-usd 2 --allow-tools "${allow[@]}" "$@"
