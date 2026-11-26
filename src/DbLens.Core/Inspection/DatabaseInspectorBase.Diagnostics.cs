using System.Data.Common;
using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Cascade;
using DbLens.Core.Analysis.Denormalization;
using DbLens.Core.Analysis.Fragmentation;
using DbLens.Core.Analysis.Gdpr;
using DbLens.Core.Analysis.Indexes;
using DbLens.Core.Models;
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

    /// <summary>
    /// A command returning schema_name, table_name, index_name and scans: the scan counter of every
    /// index of the tables.
    /// </summary>
    protected abstract DbCommand IndexUsageCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables);

    /// <summary>
    /// A command returning one row: server_start_days, created_days, restored_days,
    /// stats_reset_days (days since each event, null when unknown), restore_visible and
    /// any_index_scanned (whether any index of the database has recorded a scan). The base
    /// takes the most recent event as the start of the scan counters.
    /// </summary>
    protected abstract Task<DbCommand> StatsWindowCommandAsync(DbConnection conn, CancellationToken ct);

    private async Task<IndexUsageStats> QueryIndexUsageStatsAsync(IReadOnlyList<ScopedTable> tables, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        var rows = await ReadAllAsync(IndexUsageCommand(conn, tables), r => (
            Index: (r.Text("schema_name"), r.Text("table_name"), r.Text("index_name")),
            Scans: r.Number("scans")), ct);

        return new IndexUsageStats(rows.ToDictionary(r => r.Index, r => r.Scans), await ReadStatsWindowAsync(conn, ct));
    }

    /// <summary>How long the engine's activity counters (scans, updates) have been counting.</summary>
    private async Task<StatsWindow> ReadStatsWindowAsync(DbConnection conn, CancellationToken ct)
    {
        var window = await ReadAllAsync(await StatsWindowCommandAsync(conn, ct), r => StatsWindow.From(
            r.RealOrNull("server_start_days") ?? 0,
            r.RealOrNull("created_days"),
            r.RealOrNull("restored_days"),
            r.RealOrNull("stats_reset_days"),
            r.Flag("restore_visible"),
            r.Flag("any_index_scanned")), ct);
        return window.Single();
    }

    /// <summary>
    /// A command returning one row per index of the tables that can be measured, without measuring: schema_name,
    /// table_name, index_name, is_primary, method, page_count, and handle and handle2, the two numbers
    /// <see cref="FragmentationMeasureCommand"/> finds the index again by.
    /// </summary>
    protected abstract DbCommand FragmentationCandidatesCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables);

    /// <summary>A command returning one row for one index: fragmentation_percent and page_density_percent (null when not collected).</summary>
    protected abstract DbCommand FragmentationMeasureCommand(DbConnection conn, FragmentationCandidate index, AnalyzeFragmentationRequest request);

    /// <summary>Why physical statistics cannot be read on this database at all (a missing extension), or null.</summary>
    protected abstract Task<string?> FragmentationUnavailableAsync(DbConnection conn, CancellationToken ct);

    /// <summary>The reason a driver error means the login may not measure fragmentation, or null for any other error.</summary>
    protected abstract string? FragmentationPermissionReason(DbException ex);

    /// <summary>How the numbers were obtained, as a caveat.</summary>
    protected abstract string FragmentationSource(AnalyzeFragmentationRequest request);

    /// <summary>
    /// One measurement per index, each in its own command: the first that takes too long or fails does not take the
    /// others with it, and the call has a time budget as a whole. A missing extension or permission is an answer,
    /// not an error: <c>StatsAvailable = false</c> with the reason. This is the one place besides the probes that
    /// catches a driver error, for the same reason: an analysis that lost one index is still an analysis.
    /// </summary>
    private async Task<(IndexFragmentationStats Stats, StatsWindow? Window)> QueryFragmentationStatsAsync(
        IReadOnlyList<ScopedTable> tables, AnalyzeFragmentationRequest request, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        if (await FragmentationUnavailableAsync(conn, ct) is { } unavailable)
            return (UnavailableStats(unavailable), null);

        List<FragmentationCandidate> candidates;
        try
        {
            candidates = await ReadAllAsync(FragmentationCandidatesCommand(conn, tables), r => new FragmentationCandidate(
                r.Text("schema_name"), r.Text("table_name"), r.Text("index_name"), r.Flag("is_primary"), r.Text("method"),
                r.NumberOrNull("page_count") ?? 0, r.Number("handle"), r.NumberOrNull("handle2") ?? 0), ct);
        }
        catch (DbException ex) when (FragmentationPermissionReason(ex) is not null)
        {
            return (UnavailableStats(FragmentationPermissionReason(ex)!), null);
        }

        List<IndexFragmentation> indexes = [.. candidates.Where(c => c.PageCount < request.MinPageCount)
            .Select(c => new IndexFragmentation(c.Schema, c.Table, c.IndexName, 0, c.PageCount, null, c.IsPrimary, c.Method))];
        List<NotMeasuredIndex> notMeasured = [];

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(request.TotalBudgetSeconds));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // The largest first: they are the ones a rebuild is worth the most on.
        foreach (var c in candidates.Where(c => c.PageCount >= request.MinPageCount).OrderByDescending(c => c.PageCount).ThenBy(c => c.Schema, StringComparer.Ordinal).ThenBy(c => c.Table, StringComparer.Ordinal).ThenBy(c => c.IndexName, StringComparer.Ordinal))
        {
            if ((clock.Elapsed.TotalSeconds >= request.TotalBudgetSeconds || budget.IsCancellationRequested) && !ct.IsCancellationRequested)
            {
                notMeasured.Add(new NotMeasuredIndex(c.Schema, c.Table, c.IndexName, "over the time budget"));
                continue;
            }

            try
            {
                var cmd = FragmentationMeasureCommand(conn, c, request);
                cmd.CommandTimeout = request.IndexTimeoutSeconds;
                var (fragmentation, density) = (await ReadAllAsync(cmd, r => (Fragmentation: r.RealOrNull("fragmentation_percent"), Density: r.RealOrNull("page_density_percent")), budget.Token)).FirstOrDefault();
                indexes.Add(new IndexFragmentation(
                    c.Schema, c.Table, c.IndexName, fragmentation is { } f && double.IsFinite(f) ? f : 0, c.PageCount,
                    request.IncludePageDensity && density is { } d && double.IsFinite(d) ? d : null, c.IsPrimary, c.Method));
            }
            catch (DbException ex) when (FragmentationPermissionReason(ex) is { } denied)
            {
                return (UnavailableStats(denied), null);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                notMeasured.Add(new NotMeasuredIndex(c.Schema, c.Table, c.IndexName, "over the time budget"));
            }
            catch (DbException ex)
            {
                notMeasured.Add(new NotMeasuredIndex(c.Schema, c.Table, c.IndexName, Brief(ex)));
            }
        }

        StatsWindow? window = null;
        try
        {
            window = await ReadStatsWindowAsync(conn, ct);
        }
        catch (DbException)
        {
            // The caveat about a recent restore is lost; the measurements are not.
        }

        return (new IndexFragmentationStats(
            [.. indexes.OrderBy(i => i.Schema, StringComparer.Ordinal).ThenBy(i => i.Table, StringComparer.Ordinal).ThenBy(i => i.IndexName, StringComparer.Ordinal)],
            true, null, FragmentationSource(request), notMeasured), window);
    }

    private static IndexFragmentationStats UnavailableStats(string reason) =>
        new([], false, reason, "Physical index statistics could not be read.");

    /// <summary>The plan of a statement the guard has already passed; a null root when unparseable.</summary>
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

            // A login that cannot read the counters loses the one rule that needs them, not the analysis.
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
                    statsCaveat = $"Scan statistics could not be read ({Brief(ex)}), " +
                                  "so the 'unused-index' rule did not run.";
                }
            }

            // fk-type-mismatch compares with the referenced key, which may be outside the scope. SQL Server
            // refuses a foreign key of another type (Msg 1753, 1778): nothing to read there.
            var outside = Dialect == DatabaseDialect.SqlServer ? [] : details.Tables.SelectMany(t => t.ForeignKeysOutgoing)
                .Select(fk => (fk.ToSchema, fk.ToTable)).Distinct()
                .Where(r => !details.Tables.Any(t => (t.Schema, t.Name) == r))
                .Select(r => new ParsedName(r.ToSchema, r.ToTable, $"{r.ToSchema}.{r.ToTable}")).ToList();
            var referenced = outside.Count == 0 ? null : await GetTableDetailsAsync(outside, ct);
            var referencedCaveat = referenced is { NotFound.Count: > 0 }
                ? $"The referenced tables {string.Join(", ", referenced.NotFound)} could not be read, so the " +
                  "'fk-type-mismatch' rule did not compare the foreign keys to them."
                : null;

            var result = IndexAnalysisEngine.Analyze(details.Tables, stats, request, Dialect, referenced?.Tables);
            result = result with
            {
                Caveats = WithCaveats(result.Caveats, statsCaveat, scopeCaveat, NotFoundCaveat(details), referencedCaveat),
            };

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

            var (stats, window) = await QueryFragmentationStatsAsync(scope, request, ct);
            var result = FragmentationAnalysisEngine.Analyze(stats, request, Dialect, window);
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
            var (scope, _) = await ResolveTableScopeAsync(request.Schema, tables: null, ct);
            var details = await GetTableDetailsAsync(scope, ct);
            var dialect = Dialect.ToString();

            // Each table lists its keys in both directions, so the tables in scope are the whole graph.
            var edges = details.Tables
                .SelectMany(t => t.ForeignKeysOutgoing.Concat(t.ForeignKeysIncoming))
                .DistinctBy(fk => (fk.FromSchema, fk.FromTable, fk.ConstraintName))
                .OrderBy(fk => fk.FromSchema, StringComparer.Ordinal)
                .ThenBy(fk => fk.FromTable, StringComparer.Ordinal)
                .ThenBy(fk => fk.ConstraintName, StringComparer.Ordinal)
                .Select(fk => new CascadeAnalysisEngine.FkEdge(
                    fk.FromSchema, fk.FromTable, fk.ConstraintName, fk.FromColumns,
                    fk.ToSchema, fk.ToTable, fk.ToColumns, fk.OnDelete))
                .ToList();

            var roots = ResolveCascadeRoots(request, scope, edges);
            if (roots.Count == 0) return CascadeAnalysisEngine.Empty(dialect, details.Database);

            var result = CascadeAnalysisEngine.Analyze(
                edges, scope.ToDictionary(t => (t.Schema, t.Name), t => t.ApproximateRowCount),
                roots, request, dialect, details.Database);

            Log.LogInformation(
                "Cascade path check completed: {PathCount} paths, {FindingCount} findings",
                result.Paths.Count, result.Findings.Count);

            return result;
        });
    }

    private List<(string Schema, string Name)> ResolveCascadeRoots(
        CheckCascadePathsRequest request,
        IReadOnlyList<ScopedTable> scope,
        IReadOnlyList<CascadeAnalysisEngine.FkEdge> edges)
    {
        if (request.StartingTable is not null)
        {
            // "No cascade from this table" must never be the answer for a table that is not there.
            var known = scope.Select(t => (t.Schema, t.Name))
                .Concat(edges.SelectMany(e => new[] { (e.FromSchema, e.FromTable), (e.ToSchema, e.ToTable) }))
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
        return [.. edges
            .Where(e => e.OnDelete == "CASCADE")
            .Select(e => (e.ToSchema, e.ToTable))
            .Distinct()
            .OrderBy(t => t.ToSchema)
            .ThenBy(t => t.ToTable)];
    }

    // ── explain_query ──────────────────────────────────────────────────────────

    /// <summary>The guard runs here, where no dialect can skip it, and before any connection opens.</summary>
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
            // An estimated plan runs nothing, but a second statement after a semicolon would be executed.
            var single = StatementGuard.IsSingleStatement(request.Sql, Dialect);
            if (!single.Allowed)
                throw new ArgumentException($"Refusing to plan this input. {single.RejectionReason}");
        }

        Log.LogInformation(
            "Explaining query (analyze={Analyze}, length={Length})", request.Analyze, request.Sql?.Length ?? 0);

        return RunAsync("query explanation", async () =>
        {
            var loginCaveat = request.Analyze ? await EnsureReadOnlyLoginAsync(ct) : null;

            var fetched = await FetchQueryPlanAsync(request, ct);

            var result = QueryPlanAnalysisEngine.Analyze(
                fetched.Root, QueryPlanAnalysisEngine.RenderPlanText(fetched.Root), request.Analyze, request, Dialect);

            List<string> extra = [.. fetched.Caveats, .. loginCaveat is null ? [] : new[] { loginCaveat }];
            return extra.Count == 0 ? result : result with { Caveats = [..result.Caveats, ..extra] };
        });
    }
}
