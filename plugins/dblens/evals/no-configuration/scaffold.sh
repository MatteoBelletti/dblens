#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "$here/../../../dblens-dev/connections"; rm -f "$here/../../../dblens-dev/connections/"*.conn
