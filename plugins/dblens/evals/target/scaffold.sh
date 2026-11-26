#!/usr/bin/env bash
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
"$here/../_shared/target-container.sh"
"$here/../_shared/connect.sh" container dblens-eval-target eval-target target_db
