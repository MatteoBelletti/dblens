#!/usr/bin/env bash
# Runs the evals of this plugin against the dblens-docs build in this working tree.
#
#   plugins/dblens-docs/run-evals.sh [claude plugin eval options…]
#
# The plugin starts `dblens-docs mcp` from PATH; here PATH gets a wrapper for the working tree's build.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet build "$here/../../src/DbLens.Docs.Cli" -nologo -v q -c Debug

bin="$(mktemp -d)"
trap 'rm -rf "$bin"' EXIT
cat > "$bin/dblens-docs" <<WRAP
#!/usr/bin/env bash
exec dotnet "$here/../../src/DbLens.Docs.Cli/bin/Debug/net10.0/dblens-docs.dll" "\$@"
WRAP
chmod +x "$bin/dblens-docs"
export PATH="$bin:$PATH"

allow=(Workflow Write)
for t in get_project_inventory get_doc_slots check_doc_coverage set_doc_slot; do allow+=("mcp__plugin_dblens-docs_docs__$t"); done

cd "$here/.."
exec claude plugin eval dblens-docs --runs 1 --scaffold --trust-plugin --ablation none --no-publish --mocks off \
  --max-cost-usd 2 --allow-tools "${allow[@]}" "$@"
