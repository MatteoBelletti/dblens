#!/usr/bin/env bash
# Saves a named connection where the dblens-dev plugin's server reads them (its connections folder,
# set in its .mcp.json), so that an eval names a database without a password in any prompt.
# Same arguments as skills/audit/connection.sh. Runs only with --scaffold.
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
mkdir -p "$repo/plugins/dblens-dev/connections"
DbLens__ConnectionsPath="$repo/plugins/dblens-dev/connections" exec bash "$repo/plugins/dblens/skills/audit/connection.sh" "$@"
