#!/usr/bin/env bash
# The user has already started the detail document of Ordering.API (dblens-docs detail), as the skill allows.
set -euo pipefail
"$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/../_shared/eshop-it.sh"
dblens-docs detail "Ordering.API" . > /dev/null
