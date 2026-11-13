# Architecture

## Projects

```
DbLens.Core        interfaces, models, pure analysis engines, DatabaseInspectorBase   (no DB driver)
DbLens.Postgres    PostgresInspector  : DatabaseInspectorBase                          (Npgsql)
DbLens.SqlServer   SqlServerInspector : DatabaseInspectorBase                          (Microsoft.Data.SqlClient)
DbLens.Mcp         MCP server: Program.cs, tools, DI wiring
DbLens.Tests       xUnit: pure engine tests + integration tests against both demo databases
```

## A call, end to end

```mermaid
flowchart LR
    C[MCP client] -->|tool call| T[Tool class<br/>DbLens.Mcp/Tools]
    T -->|ResolveInspector| I[IDatabaseInspector<br/>keyed singleton or ad-hoc]
    I --> B[DatabaseInspectorBase<br/>scope · guard · errors · verification]
    B -->|catalog SQL hooks| D[(PostgreSQL / SQL Server)]
    B -->|metadata| E[Pure engine<br/>Analysis/&lt;Feature&gt;]
    E -->|Findings + Signals| B
    B -->|result| T --> C
```

1. A tool (`[McpServerToolType]`, derived from `InspectorTool`) turns MCP parameters into a request.
2. `InspectorTool.ResolveInspector` picks the keyed singleton for the dialect, or builds an ad-hoc
   inspector when the call carries its own `connectionString`.
3. `DatabaseInspectorBase` resolves the table scope, reads catalog metadata through the dialect's
   hooks, and wraps every `DbException` in a `DatabaseInspectionException`.
4. A **pure engine** in `Analysis/<Feature>/` turns metadata into findings. Engines never touch a
   connection, so every rule is unit-tested without a database.
5. The base appends caveats (scope, `NotFound`, truncation) and returns.

## The dialect line

`DatabaseInspectorBase` (`DbLens.Core/Inspection/`) is built on `System.Data.Common`, so Core needs
no driver. It owns everything that does not depend on SQL dialect:

| Base owns | Dialect supplies |
|---|---|
| scope resolution and the 20-table limit | `CreateConnection`, `DialectDisplayName`, `DefaultSchema` |
| `DbException` wrapping, `NotFound` caveat | `GetSchemaOverviewAsync`, `GetTableDetailsAsync`, `TestConnectionAsync` |
| the `explain_query` statement guard | `FetchQueryPlanAsync` (after the guard has run) |
| orchestration of every diagnostic tool | `QueryIndexUsageStatsAsync`, `QueryFragmentationStatsAsync`, `QueryCascadeGraphAsync` |
| the signal verification loop | `VerificationSetupSql`, `ClassifyVerificationError`, `VerificationCaveats` |
| join evidence: parsing, filtering own probes | `QuerySqlSourcesAsync` — views, routines, pg_stat_statements / Query Store |
| `query_with_receipt` and its cross-check | `ReadOnlyQuerySetupSql`, `ReceiptCaveats` |
| the read-only-login refusal | `DescribeWriteCapabilityAsync` |

`scan_gdpr`, `analyze_denormalization`, `check_migration_safety`, `infer_relationships` and
`build_glossary` need no catalog SQL of their own: they run on `TableDetail` metadata the base already
reads, plus the SQL sources.

## Analysis layout

```
DbLens.Core/Analysis/
  Finding.cs, Evidence.cs, Severity.cs, FindingCategory.cs   the result contract
  FindingSplit.cs        partitions facts and signals, owns the 200-finding cap and summary dictionaries
  SqlProposal.cs         SQL attached to a finding: Ddl or ReadOnlyProbe
  SqlIdentifier.cs       quoting for every generated identifier
  SqlText.cs             comment stripping, literal blanking, statement splitting
  Num.cs                 locale-invariant number formatting for finding text
  RuleIds.cs             every rule ID constant
  Verification/          probe, budget, measurement and outcome types shared by verifying tools
  Relationships/         undeclared-FK rule, JoinPredicateExtractor (joins in SQL text), engine
  Receipts/              receipt models, ResultComparer (cross-checks), ResultValue
  Glossary/              GlossaryEngine: the SKILL.md data map
  Indexes/ Gdpr/ Cascade/ Denormalization/ Fragmentation/ QueryPlan/ Migration/
```

## Signal verification pipeline

Three phases; only the middle one touches a database.

```mermaid
flowchart LR
    A[Engine.Analyze<br/>signals] --> P[PlanBuilder<br/>probes + eligibility]
    P --> X[Inspector runs probes<br/>read-only, rolled back]
    X --> V[Verifier<br/>reclassify]
    V --> R[Findings: Measured<br/>Signals: still inferred]
```

- `DenormalizationAnalysisEngine` / `RelationshipAnalysisEngine` produce signals — pure.
- The plan builder turns verifiable signals into `VerificationProbe`s; `VerificationBudget.Evaluate`
  decides which may run (query cap, table size, unknown row count) — pure.
- `DatabaseInspectorBase.RunVerificationProbesAsync` executes them, one rolled-back transaction each.
- `DenormalizationVerifier` / `RelationshipAnalysisEngine.ApplyVerification` reclassify — pure.

See [safety.md](safety.md#signal-verification) for the guarantees.

## Server layer

`DbLens.Mcp` holds what is not about databases:

- `ServerPolicy` — operator settings that change the contract (`AllowQueryResults`,
  `AllowWriteCapableLogin`, `ReadOnlyLoginOnLocalhost`). Configuration only; never a tool parameter a
  model could set.
- `CertifiedQueryStore` — the certified queries, one local JSON file.
- `ToolErrors` — a call-tool filter. The SDK hides exception messages; this one turns
  `ArgumentException`, `ExecutionRefusedException` and `DatabaseInspectionException` into readable tool
  errors so the model sees *why* a call was refused.

## Testing

- **Engine tests** build metadata in memory with `TestSchema` and need no database.
- **Integration tests** are written once in an abstract `<Feature>TestsBase(IDatabaseFixture)` and
  derived for PostgreSQL and SQL Server.
- **Parity tests** run the same analysis on both dialects and compare findings; genuine differences
  (SQL Server rejects a self-referencing `ON DELETE CASCADE`) are asserted as differences.
- Integration tests **skip, never pass**, when a database is unreachable, and CI fails on any skip.
- **Composition tests** (`CompositionTestsBase`) feed one tool's output to another: the constraint
  `infer_relationships` proposes and the index `analyze_indexes` proposes must be recognized — and not
  flagged — by `check_migration_safety`; a verification probe must pass `explain_query` and reproduce
  its counts through `query_with_receipt`; the glossary must list every path of the relationship map.
- Integration tests connect as `dblens_reader`; the admin login (`DBLENS_TEST_*_ADMIN_CONNECTION`) is
  used for setup, to show that a local server is exempt from the read-only-login check, and — with
  `ReadOnlyLoginOnLocalhost` — that a write-capable login is refused.
- `demo_good` is the acceptance schema: every diagnostic tool must report nothing there except its
  always-present summary finding.

## Demo databases

`docker compose up -d` starts PostgreSQL 17 on port 5433 and SQL Server 2022 on 1434, initialized
from `db/<dialect>/01-schema.sql` and `02-seed.sql`.

| Schema | Content |
|---|---|
| `public` / `dbo` | An e-commerce schema with deliberate flaws: unindexed FKs, duplicate indexes, plaintext PII, cascade chains, cached aggregates, undeclared references on `shipments` — `ship_to` visible only through the `shipment_destinations` view — and comments on `orders` for the glossary |
| `demo_good` | A clean design every tool should pass |

Seed row counts are deterministic: `users` 20, `products` 30, `orders` 40, `order_items` 80,
`payments` 35, `item_reviews` 20, `addresses` 20, `audit_logs` 100, `customer_profiles` 10,
`profile_notes` 5, `shipments` 6, `categories` 4.

`03-reader.sql` creates `dblens_reader`, the read-only login dblens connects with by default.

Volumes hold demo data only; `docker compose down -v && docker compose up -d` recreates them.

## Releases

Pushing a `v*` tag runs `.github/workflows/release.yml`: engine tests, self-contained single-file
binaries for `win-x64`, `linux-x64` and `osx-arm64`, one Claude Desktop extension per platform
(`.mcpb`, from `packaging/mcpb/manifest.json`), the NuGet tool package (`dotnet tool install -g
DbLens.Mcp`, also typed `McpServer` with `.mcp/server.json`), and the GitHub release. The package is
pushed to nuget.org only when the `NUGET_API_KEY` secret exists.
