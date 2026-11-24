#!/usr/bin/env bash
# All the sources of dotnet/eShop (the test fixture), scaffolded in Italian by dblens-docs, at the root of the workspace.
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
cp -r "$repo/tests/DbLens.Tests/Fixtures/Docs/eShopFlows/." .
rm -f README.md
printf '{ "language": "it" }\n' > dblens-docs.json
dblens-docs scaffold . > /dev/null
