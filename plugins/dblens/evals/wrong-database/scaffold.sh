#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
"$here/../_shared/connect.sh" demo PostgreSql eval-demo-pg
