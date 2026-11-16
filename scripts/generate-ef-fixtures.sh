#!/usr/bin/env bash
# Regenerates the EF Core snapshot fixtures dblens's parser is tested against, with the real
# `dotnet ef` of each supported major, because the snapshot format changes between them.
#
#   scripts/generate-ef-fixtures.sh            # every major
#   scripts/generate-ef-fixtures.sh 9          # one
#
# For each major and provider the fixture holds two migrations (Init, from the ShopStage=1 model,
# then AddPayments), the final snapshot, and the SQL script of each migration.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
sample="$root/samples/DemoShop.EfCore"
fixtures="$root/tests/DbLens.Tests/Fixtures/EfSnapshots"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

declare -A ef=([8]=8.0.34 [9]=9.0.23 [10]=10.0.15)
majors=("${@:-8 9 10}")
read -r -a majors <<< "${majors[*]}"

for major in "${majors[@]}"; do
  version="${ef[$major]}"
  tool="$work/tools/$major"
  dotnet tool install dotnet-ef --version "$version" --tool-path "$tool" > /dev/null

  for provider in PostgreSql SqlServer; do
    context="Shop${provider/PostgreSql/Postgres}Context"
    project="$work/ef$major-$provider"
    cp -r "$sample" "$project"
    rm -rf "$project/bin" "$project/obj" "$project/Migrations"

    build() { dotnet build "$project" -p:EfMajor="$major" "$@" -v quiet -nologo > "$work/build.log" || { cat "$work/build.log"; exit 1; }; }
    migrate() { "$tool/dotnet-ef" migrations "$@" --project "$project" --context "$context" --no-build; }

    build -p:ShopStage=1
    migrate add Init --output-dir Migrations > /dev/null
    build
    migrate add AddPayments --output-dir Migrations > /dev/null
    build

    out="$fixtures/ef$major/$provider"
    rm -rf "$out" && mkdir -p "$out"
    cp "$project"/Migrations/*.cs "$out/"
    init="$(basename "$(ls "$project"/Migrations/*_Init.cs)" .cs)"
    migrate script 0 "$init" --output "$out/Init.sql" > /dev/null
    migrate script "$init" AddPayments --output "$out/AddPayments.sql" > /dev/null
    echo "ef$major $provider: $(ls "$out" | tr '\n' ' ')"
  done
done
