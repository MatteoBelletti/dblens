using System.Data.Common;
using DbLens.Core.Analysis;
using DbLens.Core.Analysis.HotQueries;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Analysis.Verification;

namespace DbLens.Core.Inspection;

/// <summary>
/// The heaviest queries the engine has counted. A dialect reads its statistics source; the base
/// picks the application's queries out of it, derives the figures and applies the rules.
/// </summary>
public abstract partial class DatabaseInspectorBase
{
    /// <summary>
    /// The engine's query statistics, on an open connection: the source's name, a command returning
    /// up to @fetch rows heaviest by total time first — query_id, query_text, calls, total_time_ms,
    /// rows_returned, pages_read, pages_from_disk, temp_pages, plan_key — and one returning the days
    /// they cover; or no source and why. A source that is off is an answer, not an error; a view the
    /// login is refused is caught by the base.
    /// </summary>
    protected abstract Task<HotQueryCommands> HotQueryCommandsAsync(DbConnection conn, CancellationToken ct);

    /// <param name="Name">"pg_stat_statements", "Query Store", "plan cache"; null when there is none.</param>
    /// <param name="Notes">One sentence per source missing or partly visible, with what would enable it.</param>
    protected sealed record HotQueryCommands(string? Name, DbCommand? Queries, DbCommand? Window, IReadOnlyList<string> Notes);

    /// <summary>
    /// The estimated plan of a listed query, or why there is none. Never executes it: a stored plan
    /// (SQL Server) or a generic plan of the normalized text (PostgreSQL).
    /// </summary>
    protected abstract Task<(QueryPlanNode? Root, string? Caveat)> PlanHotQueryAsync(HotQueryStats query, CancellationToken ct);

    public Task<FindHotQueriesResult> FindHotQueriesAsync(FindHotQueriesRequest request, CancellationToken ct = default) =>
        RunAsync("hot queries", async () =>
        {
            var (name, days, statistics, notes) = await ReadHotQueriesAsync(request.Fetch, ct);
            var availability = new HotQuerySourceInfo(name, days, notes);
            if (name is null) return HotQueryEngine.Build([], [], availability, 0, []);

            var (tables, scopeCaveat) = await ResolveTableScopeAsync(null, null, ct, includePartitions: true);
            var ranked = HotQueryEngine.Rank(statistics, [.. tables.Select(t => (t.Schema, t.Name))], Dialect, DefaultSchema, request);

            List<Finding> findings = [.. HotQueryEngine.StatisticsFindings(ranked, request, Dialect)];
            List<string> caveats = scopeCaveat is null ? [] : [scopeCaveat];

            var plansRead = 0;
            foreach (var (stats, query, _) in ranked.Take(request.PlanTop))
            {
                if (await PlanFindingsAsync(stats, query, caveats, ct) is not { } planFindings) continue;
                plansRead++;
                findings.AddRange(planFindings);
            }

            var (fanOut, verification) = await JoinFanOutAsync(ranked, tables, request, caveats, ct);
            findings.AddRange(fanOut);

            return HotQueryEngine.Build(ranked, findings, availability, plansRead, [.. caveats.Distinct()], verification);
        });

    private async Task<(string? Name, double? Days, IReadOnlyList<HotQueryStats> Queries, IReadOnlyList<string> Notes)> ReadHotQueriesAsync(
        int fetch, CancellationToken ct)
    {
        // Opened outside the catch: a login that fails is an error, not a missing source.
        await using var conn = await OpenAsync(ct);
        try
        {
            var source = await HotQueryCommandsAsync(conn, ct);
            if (source is not { Name: { } name, Queries: { } command }) return (null, null, [], source.Notes);

            AddParameter(command, "fetch", fetch);

            var queries = await ReadAllAsync(command, r => new HotQueryStats(
                // An encrypted SQL Server module's text reads as NULL.
                r.Text("query_id"), r.TextOrNull("query_text") ?? "", r.Number("calls"), r.RealOrNull("total_time_ms") ?? 0,
                r.Number("rows_returned"), r.Number("pages_read"), r.Number("pages_from_disk"), r.NumberOrNull("temp_pages"),
                r.TextOrNull("plan_key")), ct);

            double? days = null;
            if (source.Window is { } window)
                await using (window)
                    days = await window.ExecuteScalarAsync(ct) is { } value and not DBNull
                        ? Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)
                        : null;

            return (name, days, queries, source.Notes);
        }
        catch (DbException ex)
        {
            return (null, null, [], [$"The query statistics could not be read ({ex.Message.ReplaceLineEndings(" ").Trim()})."]);
        }
    }

    /// <summary>The plan rules of explain_query on one listed query; null when its plan could not be had.</summary>
    private async Task<IEnumerable<Finding>?> PlanFindingsAsync(
        HotQueryStats stats, HotQuery query, List<string> caveats, CancellationToken ct)
    {
        // The text came from the engine, not a caller, but it is still planned as one statement only.
        if (!StatementGuard.IsSingleStatement(stats.Text, Dialect).Allowed) return null;

        (QueryPlanNode? Root, string? Caveat) plan;
        try
        {
            plan = await PlanHotQueryAsync(stats, ct);
        }
        catch (DbException ex)
        {
            // One query the planner cannot take (a $1 whose type it cannot infer) costs its plan, not the list.
            caveats.Add($"The plan of query {query.QueryId} could not be read: {ex.Message.ReplaceLineEndings(" ").Trim()}");
            return null;
        }

        if (plan.Caveat is not null) caveats.Add(plan.Caveat);
        if (plan.Root is not { } root) return null;

        var analysis = QueryPlanAnalysisEngine.Analyze(Redacted(root), "", executed: false, new ExplainQueryRequest(query.Text), Dialect);
        return analysis.Findings.Concat(analysis.Signals).Select(f => f with
        {
            Metadata = new Dictionary<string, object>(f.Metadata ?? new Dictionary<string, object>()) { ["queryId"] = query.QueryId },
        });
    }

    /// <summary>A stored plan keeps the literals and sniffed values of its predicates: they go, as from the text.</summary>
    private QueryPlanNode Redacted(QueryPlanNode node) => node with
    {
        Filter = node.Filter is null ? null : SqlText.Redact(node.Filter, Dialect),
        Children = [.. node.Children.Select(Redacted)],
    };

    /// <summary>
    /// Joins of one parent to two children among the listed queries: signals, measured when asked.
    /// Only the tables the queries name are read from the catalog.
    /// </summary>
    private async Task<(IReadOnlyList<Finding> Findings, VerificationSummary? Verification)> JoinFanOutAsync(
        IReadOnlyList<HotQueryEngine.Ranked> ranked, IReadOnlyList<ScopedTable> tables, FindHotQueriesRequest request,
        List<string> caveats, CancellationToken ct)
    {
        var named = ranked.SelectMany(r => r.Tables).ToHashSet();
        var scope = tables.Where(t => named.Contains((t.Schema, t.Name))).ToList();
        if (scope.Count == 0) return ([], request.VerifyJoins ? VerificationSummary.From([], attempted: true) : null);

        var details = await GetTableDetailsAsync(scope, ct);
        if (NotFoundCaveat(details) is { } notFound) caveats.Add(notFound);
        var shapes = JoinFanOut.Detect(ranked, details.Tables, DefaultSchema, Dialect)
            .DistinctBy(s => s.Target.Key)
            .ToList();
        var signals = shapes.Select(JoinFanOut.ToSignal).ToList();

        if (!request.VerifyJoins) return (signals, null);

        var rowCounts = scope.ToDictionary(t => (t.Schema, t.Name), t => t.ApproximateRowCount);
        return await VerifyEachAsync(
            [.. shapes.Zip(signals)], p => JoinFanOut.Probe(p.First, rowCounts, Dialect), (p, m) => JoinFanOut.Apply(p.Second, m),
            request.Budget, "A join whose two child tables are both empty compared nothing, so it is reported as not measured, not as refuted.",
            caveats, ct);
    }
}
