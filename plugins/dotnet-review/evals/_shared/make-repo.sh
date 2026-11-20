#!/usr/bin/env bash
# Builds a case's repository in the workspace: the EF Core sample plus the case's base/ files on
# main, then its pr/ files on the branch under review. Each case's scaffold.sh calls it.
set -euo pipefail
case_dir="$1"
branch="$2"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
git() { command git -c user.name=eval -c user.email=eval@example.invalid "$@"; }

cp -r "$repo/samples/DemoShop.EfCore" DemoShop.EfCore
rm -rf DemoShop.EfCore/bin DemoShop.EfCore/obj
if [[ -d $case_dir/base ]]; then cp -r "$case_dir/base/." .; fi
git init -q -b main
git add -A
git commit -q -m "Base"
git checkout -q -b "$branch"
cp -r "$case_dir/pr/." .
git add -A
git commit -q -m "$branch"
