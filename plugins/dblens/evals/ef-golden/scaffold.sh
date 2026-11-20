#!/usr/bin/env bash
# Copies the EF Core demo sample into the case's workspace and builds it once, so that the ef agent's
# `dotnet ef migrations script` builds offline. dotnet finds a local tool from the current folder up,
# and the agent runs from the workspace: the repository's manifest goes there. Runs only with --scaffold.
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
cp -r "$repo/samples/DemoShop.EfCore" DemoShop.EfCore
rm -rf DemoShop.EfCore/bin DemoShop.EfCore/obj
cp -r "$repo/.config" .config
dotnet tool restore > /dev/null
dotnet build DemoShop.EfCore -v q -nologo > /dev/null
