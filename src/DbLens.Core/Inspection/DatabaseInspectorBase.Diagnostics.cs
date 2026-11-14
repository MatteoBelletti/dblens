using System.Data.Common;
using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Cascade;
using DbLens.Core.Analysis.Denormalization;
using DbLens.Core.Analysis.Fragmentation;
using DbLens.Core.Analysis.Gdpr;
using DbLens.Core.Analysis.Indexes;
using DbLens.Core.Analysis.Migration;
using DbLens.Core.Analysis.QueryPlan;
using Microsoft.Extensions.Logging;

namespace DbLens.Core.Inspection;

/// <summary>
/// The diagnostic operations. Each is the same shape — resolve scope, gather catalog data, hand
/// it to a pure engine — and only the data-gathering hooks are dialect-specific.
/// </summary>
public abstract partial class DatabaseInspectorBase
{
    // ── Dialect hooks ──────────────────────────────────────────────────────────

    /// <summary>Scan counters per index, and the server uptime they accumulated over.</summary>
    protected abstract Task<IndexUsageStats> QueryIndexUsageStatsAsync(
        IReadOnlyList<ScopedTable> tables, CancellationToken ct);

    /// <summary>
    /// Physical fragmentation per index. Must return <c>StatsAvailable = false</c> with a reason,
    /// rather than throw, when the engine simply cannot provide the numbers (a missing extension,
    /// a missing permission): that outcome is an answer, not an error.
    /// </summary>
    protected abstract Task<IndexFragmentationStats> QueryFragmentationStatsAsync(
        IReadOnlyList<ScopedTable> tables, AnalyzeFragmentationRequest request, CancellationToken ct);

    /// <summary>The foreign-key graph, row counts and database name for cascade analysis.</summary>
    protected abstract Task<CascadeGraph> QueryCascadeGraphAsync(string? schema, CancellationToken ct);

    protected sealed record CascadeGraph(
        string DatabaseName,
        IReadOnlyList<CascadeAnalysisEngine.FkEdge> Edges,
        IReadOnlyDictionary<(string Schema, string Name), long?> RowCounts);

    /// <summary>
    /// The plan for a statement that has already passed the guard when Analyze is set.
    /// Returns the parsed tree (null when unparseable) and any dialect-specific caveats.
    /// </summary>
    protected abstract Task<QueryPlanFetch> FetchQueryPlanAsync(ExplainQueryRequest request, CancellationToken ct);

    protected sealed record QueryPlanFetch(QueryPlanNode? Root, IReadOnlyList<string> Caveats);

    // ── analyze_indexes ────────────────────────────────────────────────────────

    public Task<AnalyzeIndexesResult> AnalyzeIndexesAsync(
        AnalyzeIndexesRequest request, CancellationToken ct = default)
    {
        Log.LogInformation(
            "Starting index analysis (schema={Schema}, tables={Tables}, includeUnused={IncludeUnused})",
            request.Schema ?? "*", Describe(request.Tables), request.IncludeUnusedIndexes);

        return RunAsync("index analysis", async () =>
        {
            var (scope, scopeCaveat) = await ResolveTableScopeAsync(request.Schema, request.Tables, ct);
            if (scope.Count == 0) return IndexAnalysisEngine.Empty() with { Caveats = [scopeCaveat!] };

            var details = await GetTableDetailsAsync(scope, ct);

            // The structural rules need only the catalog; a login that cannot read the counters
            // loses the one rule that needs them, not the analysis.
            IndexUsageStats? stats = null;
            string? statsCaveat = null;
            if (request.IncludeUnusedIndexes)
            {
                try
                {
                    stats = await QueryIndexUsageStatsAsync(scope, ct);
                }
                catch (DbException ex)
                {
                    statsCaveat = $"Scan statistics could not be read ({ex.Message.ReplaceLineEndings(" ").Trim()}), " +
                                  "so the 'unused-index' rule did not run.";
                }
            }

            var result = IndexAnalysisEngine.Analyze(details.Tables, stats, request, Dialect);
            result = result with { Caveats = WithCaveats(result.Caveats, statsCaveat, scopeCaveat, NotFoundCaveat(details)) };

            Log.LogInformation(
                "Index analysis completed: {FindingCount} findings, {Caveats} caveats",
                result.Findings.Count, result.Caveats.Count);

            return result;
        });
    }

    // ── scan_gdpr ──────────────────────────────────────────────────────────────

    public Task<ScanGdprResult> ScanGdprAsync(ScanGdprRequest request, CancellationToken ct = default)
    {
        Log.LogInformation(
            "Starting GDPR scan (schema={Schema}, tables={Tables}, minConfidence={MinConfidence})",
            request.Schema ?? "*", Describe(request.Tables), request.MinConfidence);

        return RunAsync("GDPR scan", async () =>
        {
            var (scope, scopeCaveat) = await ResolveTableScopeAsync(
                request.Schema, request.Tables, ct, request.ExcludeTables);
            if (scope.Count == 0)
                return GdprAnalysisEngine.Empty() with { Caveats = WithCaveats([], scopeCaveat) };

            var details = await GetTableDetailsAsync(scope, ct);

            var result = GdprAnalysisEngine.Analyze(details.Tables, request, Dialect);
            result = result with { Caveats = WithCaveats(result.Caveats, scopeCaveat, NotFoundCaveat(details)) };

            Log.LogInformation("GDPR scan completed: {SignalCount} signals", result.Signals.Count);
            return result;
        });
    }

    // ── analyze_denormalization ────────────────────────────────────────────────

    public Task<AnalyzeDenormalizationResult> AnalyzeDenormalizationAsync(
        AnalyzeDenormalizationRequest request, CancellationToken ct = default)
    {
        Log.LogInformation(
            "Starting denormalization analysis (schema={Schema}, tables={Tables}, verify={Verify})",
            request.Schema ?? "*", Describe(request.Tables), request.VerifySignals);

        return RunAsync("denormalization analysis", async () =>
        {
            var (scope, scopeCaveat) = await ResolveTableScopeAsync(request.Schema, request.Tables, ct);
            if (scope.Count == 0) return DenormalizationAnalysisEngine.Empty() with { Caveats = [scopeCaveat!] };

            var details = await GetTableDetailsAsync(scope, ct);
            var result = DenormalizationAnalysisEngine.Analyze(details.Tables, request, Dialect);

            if (request.VerifySignals)
                result = await VerifyDenormalizationAsync(result, details.Tables, scope, request, ct);

            result = result with { Caveats = WithCaveats(result.Caveats, scopeCaveat, NotFoundCaveat(details)) };

            Log.LogInformation(
                "Denormalization analysis completed: {Findings} findings, {Signals} signals across {Tables} table(s)",
                result.Findings.Count, result.Signals.Count, details.Tables.Count);

            return result;
        });
    }

    // ── analyze_fragmentation ──────────────────────────────────────────────────

    public Task<AnalyzeFragmentationResult> AnalyzeFragmentationAsync(
        AnalyzeFragmentationRequest request, CancellationToken ct = default)
    {
        Log.LogInformation(
            "Starting fragmentation analysis (schema={Schema}, tables={Tables}, minPages={MinPages})",
            request.Schema ?? "*", Describe(request.Tables), request.MinPageCount);

        return RunAsync("fragmentation analysis", async () =>
        {
            // The partitions hold the pages; the partitioned table itself stores nothing.
            var (scope, scopeCaveat) = await ResolveTableScopeAsync(
                request.Schema, request.Tables, ct, includePartitions: true);
            if (scope.Count == 0) return FragmentationAnalysisEngine.Empty() with { Caveats = [scopeCaveat!] };

            var stats = await QueryFragmentationStatsAsync(scope, request, ct);
            var result = FragmentationAnalysisEngine.Analyze(stats, request, Dialect);
            result = result with { Caveats = WithCaveats(result.Caveats, scopeCaveat) };

            Log.LogInformation(
                "Fragmentation analysis completed: {FindingCount} findings, statsAvailable={Available}",
                result.Findings.Count, result.StatsAvailable);

            return result;
        });
    }

    // ── check_migration_safety ─────────────────────────────────────────────────

    public Task<CheckMigrationSafetyResult> CheckMigrationSafetyAsync(
        CheckMigrationSafetyRequest request, CancellationToken ct = default)
    {
        Log.LogInformation(
            "Checking migration safety (length={Length}, defaultSchema={Schema})",
            request.Ddl?.Length ?? 0, request.DefaultSchema ?? DefaultSchema);

        return RunAsync("migration safety check", async () =>
        {
            var statements = DdlClassifier.Classify(request.Ddl ?? "", Dialect);
            if (statements.Count == 0) return MigrationSafetyEngine.Empty();

            // Every user table, not just a filtered scope: a script can touch any schema.
            var catalog = await ResolveTableScopeAsync(schema: null, tables: null, ct, includePartitions: true);
            var tables = catalog.Tables.ToDictionary(
                t => (t.Schema, t.Name),
                t => new MigrationTableContext(
                    t.Schema, t.Name, t.ApproximateRowCount, t.TotalSizeBytes, t.IncomingForeignKeys));

            var result = MigrationSafetyEngine.Analyze(
                statements, tables, request with { DefaultSchema = request.DefaultSchema ?? DefaultSchema }, Dialect);

            Log.LogInformation(
                "Migration safety check completed: {Statements} statement(s), {Findings} finding(s)",
                statements.Count, result.Findings.Count);

            return result;
        });
    }

    // ── check_cascade_paths ────────────────────────────────────────────────────

    public Task<CheckCascadePathsResult> CheckCascadePathsAsync(
        CheckCascadePathsRequest request, CancellationToken ct = default)
    {
        Log.LogInformation(
            "Starting cascade path check (startingTable={StartingTable}, schema={Schema}, maxDepth={MaxDepth})",
            request.StartingTable ?? "*", request.Schema ?? "*", request.MaxDepth);

        return RunAsync("cascade path check", async () =>
        {
            var graph = await QueryCascadeGraphAsync(request.Schema, ct);
            var dialect = Dialect.ToString();

            var roots = ResolveCascadeRoots(request, graph);
            if (roots.Count == 0) return CascadeAnalysisEngine.Empty(dialect, graph.DatabaseName);

            var result = CascadeAnalysisEngine.Analyze(
                graph.Edges, graph.RowCounts, roots, request, dialect, graph.DatabaseName);

            Log.LogInformation(
                "Cascade path check completed: {PathCount} paths, {FindingCount} findings",
                result.Paths.Count, result.Findings.Count);

            return result;
        });
    }

    private List<(string Schema, string Name)> ResolveCascadeRoots(
        CheckCascadePathsRequest request, CascadeGraph graph)
    {
        if (request.StartingTable is not null)
        {
            // "No cascade from this table" must never be the answer for a table that is not there.
            var known = graph.RowCounts.Keys
                .Concat(graph.Edges.SelectMany(e => new[] { (e.FromSchema, e.FromTable), (e.ToSchema, e.ToTable) }))
                .ToHashSet();
            var readings = ParseTableNames([request.StartingTable], defaultSchema: null)
                .Select(n => (n.Schema, n.Name))
                .ToList();

            var exact = readings.Where(known.Contains).Take(1).ToList();
            if (exact.Count == 1) return exact;

            var anyCase = known.Where(k => readings.Contains(k, CaseInsensitivePairComparer.Instance)).Take(2).ToList();
            if (anyCase.Count == 1) return anyCase;

            throw new ArgumentException(
                $"Table '{request.StartingTable}' was not found" +
                (request.Schema is null ? "" : $" in schema '{request.Schema}'") +
                ". Pass it as 'schema.table', exactly as get_schema lists it.");
        }

        // Every table that is the parent of at least one CASCADE foreign key.
        return graph.Edges
            .Where(e => e.OnDelete == "CASCADE")
            .Select(e => (e.ToSchema, e.ToTable))
            .Distinct()
            .OrderBy(t => t.ToSchema)
            .ThenBy(t => t.ToTable)
            .ToList();
    }

    // ── explain_query ──────────────────────────────────────────────────────────

    /// <summary>
    /// The statement guard lives here, not in the dialects, so that no dialect can skip it.
    /// It runs before any connection is opened: hostile input never reaches the database.
    /// </summary>
    public Task<ExplainQueryResult> ExplainQueryAsync(ExplainQueryRequest request, CancellationToken ct = default)
    {
        if (request.Analyze)
        {
            var guard = StatementGuard.IsReadOnlySelect(request.Sql, Dialect);
            if (!guard.Allowed)
                throw new ArgumentException(
                    $"Refusing to execute this statement with ANALYZE. {guard.RejectionReason}");
        }
        else
        {
            // An estimated plan runs nothing, whatever the statement — but only that statement:
            // anything after a semicolon would be sent as a statement of its own and executed.
            var single = StatementGuard.IsSingleStatement(request.Sql, Dialect);
            if (!single.Allowed)
                throw new ArgumentException($"Refusing to plan this input. {single.RejectionReason}");
        }

        Log.LogInformation(
            "Explaining query (analyze={Analyze}, length={Length})", request.Analyze, request.Sql?.Length ?? 0);

        return RunAsync("query explanation", async () =>
        {
            if (request.Analyze) await EnsureReadOnlyLoginAsync(ct);

            var fetched = await FetchQueryPlanAsync(request, ct);

            var result = QueryPlanAnalysisEngine.Analyze(
                fetched.Root, QueryPlanAnalysisEngine.RenderPlanText(fetched.Root), request.Analyze, request, Dialect);

            return fetched.Caveats.Count == 0
                ? result
                : result with { Caveats = [..result.Caveats, ..fetched.Caveats] };
        });
    }
}
