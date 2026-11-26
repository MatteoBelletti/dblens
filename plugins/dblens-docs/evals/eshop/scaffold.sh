#!/usr/bin/env bash
# The trimmed dotnet/eShop checkout from the test fixtures, scaffolded by dblens-docs, at the root of the workspace (the server works on the folder it starts in). Runs only with --scaffold.
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
cp -r "$repo/tests/DbLens.Tests/Fixtures/Docs/eShop/." .
dblens-docs scaffold . > /dev/null
