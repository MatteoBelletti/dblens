#!/usr/bin/env bash
# A small web API, scaffolded by dblens-docs, with one slot already a person's. Needs dblens-docs on PATH
# (run-evals.sh puts the working tree's build there). Runs only with --scaffold.
set -euo pipefail
shared="$(cd "$(dirname "${BASH_SOURCE[0]}")/../_shared" && pwd)"
cp -r "$shared/shop-api" shop-api
dblens-docs scaffold . > /dev/null
python3 - <<'PY'
import re
p = "docs/project/api.md"
s = open(p).read()
s = re.sub(r"(<!-- dblens:slot endpoint:GET /health [^\n]*-->\n)", r"\1Written by a person.\n", s, count=1)
open(p, "w").write(s)
PY
dblens-docs confirm "endpoint:GET /health" . > /dev/null
