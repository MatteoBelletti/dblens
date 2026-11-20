#!/usr/bin/env bash
# Copies the EF Core demo sample into the case's workspace and builds it once, so that the ef agent's
# `dotnet ef migrations script` builds offline. Runs only with --scaffold.
set -euo pipefail
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)"
cp -r "$repo/samples/DemoShop.EfCore" DemoShop.EfCore
rm -rf DemoShop.EfCore/bin DemoShop.EfCore/obj
cd DemoShop.EfCore
dotnet tool restore > /dev/null
dotnet build -v q -nologo > /dev/null
