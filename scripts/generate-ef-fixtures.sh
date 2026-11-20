#!/usr/bin/env bash
# Regenerates the EF Core snapshot fixtures dblens's parser is tested against, with the real
# `dotnet ef` of each supported major, because the snapshot format changes between them.
#
#   scripts/generate-ef-fixtures.sh            # every major
#   scripts/generate-ef-fixtures.sh 9          # one
#
# For each major and provider the fixture holds three migrations (Init and AddPayments, from the
# ShopStage=1 and 2 models, then DropUserDateOfBirth), the final snapshot, and the SQL script of each.
# The EF Core 10 migrations are also copied into the sample, and its Init script into the demo
# database (db/*/04-shop.sql), where the drift below that file's "-- Drift" line is kept.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
sample="$root/samples/DemoShop.EfCore"
fixtures="$root/tests/DbLens.Tests/Fixtures/EfSnapshots"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# The hand-kept drift of the demo's shop schema, read before anything is rewritten.
declare -A drift
for engine in postgres sqlserver; do
  drift[$engine]="$(sed -n '/^-- Drift/,$p' "$root/db/$engine/04-shop.sql" 2>/dev/null || true)"
  [[ -n ${drift[$engine]} ]] || { echo "db/$engine/04-shop.sql has no '-- Drift' line to keep" >&2; exit 1; }
done

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
    migrations="$project/Migrations/$provider"

    build -p:ShopStage=1
    migrate add Init --output-dir "Migrations/$provider" > /dev/null
    build -p:ShopStage=2
    migrate add AddPayments --output-dir "Migrations/$provider" > /dev/null
    build
    migrate add DropUserDateOfBirth --output-dir "Migrations/$provider" > /dev/null
    build

    out="$fixtures/ef$major/$provider"
    rm -rf "$out" && mkdir -p "$out"
    cp "$migrations"/*.cs "$out/"
    migrate script 0 Init --output "$out/Init.sql" > /dev/null
    migrate script Init AddPayments --output "$out/AddPayments.sql" > /dev/null
    migrate script AddPayments DropUserDateOfBirth --output "$out/DropUserDateOfBirth.sql" > /dev/null
    echo "ef$major $provider: $(ls "$out" | tr '\n' ' ')"

    if [[ $major == 10 ]]; then
      rm -rf "$sample/Migrations/$provider" && mkdir -p "$sample/Migrations/$provider"
      cp "$migrations"/*.cs "$sample/Migrations/$provider/"
      engine="$( [[ $provider == PostgreSql ]] && echo postgres || echo sqlserver )"
      { echo "-- The EF Core demo model (samples/DemoShop.EfCore) at its first migration, written by"
        echo "-- scripts/generate-ef-fixtures.sh, then drift for compare_ef_model to find. Edit only below \"-- Drift\"."
        LC_ALL=C sed $'1s/^\xEF\xBB\xBF//' "$out/Init.sql"
        echo
        echo "${drift[$engine]}"; } > "$root/db/$engine/04-shop.sql"
    fi
  done
done
