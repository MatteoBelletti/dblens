using System.Data.Common;
using DbLens.Core.Analysis;
using DbLens.Core.Analysis.HotQueries;
using DbLens.Core.Analysis.QueryPlan;

namespace DbLens.Core.Inspection;

/// <summary>
/// The heaviest queries the engine has counted. A dialect reads its statistics source; the base
/// picks the application's queries out of it, derives the figures and applies the rules.
/// </summary>
public abstract partial class DatabaseInspectorBase
{
    /// <summary>
    /// Up to <paramref name="fetch"/> queries from the engine's statistics, heaviest by total time
    /// first, with the source's name and window, or no source and why. Must not fail for a missing
    /// source: say what it needs instead.
    /// </summary>
    protected abstract Task<HotQuerySource> QueryHotQueriesAsync(int fetch, CancellationToken ct);

    /// <summary>
    /// The estimated plan of a listed query, or why there is none. Never executes it: a stored plan
    /// (SQL Server) or a generic plan of the normalized text (PostgreSQL).
    /// </summary>
    protected abstract Task<(QueryPlanNode? Root, string? Caveat)> PlanHotQueryAsync(HotQueryStats query, CancellationToken ct);

    public Task<FindHotQueriesResult> FindHotQueriesAsync(FindHotQueriesRequest request, CancellationToken ct = default) =>
        RunAsync("hot queries", async () =>
        {
            var source = await QueryHotQueriesAsync(request.Fetch, ct);
            var availability = new HotQuerySourceInfo(source.Name, source.WindowDays, source.Missing);
            if (source.Name is null) return HotQueryEngine.Build([], [], availability, 0, []);

            var tables = (await ResolveTableScopeAsync(null, null, ct, includePartitions: true)).Tables;
            var ranked = HotQueryEngine.Rank(source.Queries, [.. tables.Select(t => (t.Schema, t.Name))], Dialect, request);

            List<Finding> findings = [.. HotQueryEngine.StatisticsFindings(ranked, request)];
            List<string> caveats = [];

            var plansRead = 0;
            foreach (var (stats, query, _) in ranked.Take(request.PlanTop))
            {
                if (await PlanFindingsAsync(stats, query, caveats, ct) is not { } planFindings) continue;
                plansRead++;
                findings.AddRange(planFindings);
            }

            return HotQueryEngine.Build(ranked, findings, availability, plansRead, [.. caveats.Distinct()]);
        });

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
}
