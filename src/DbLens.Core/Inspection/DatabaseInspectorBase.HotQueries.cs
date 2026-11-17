using DbLens.Core.Analysis;
using DbLens.Core.Analysis.HotQueries;

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

    public Task<FindHotQueriesResult> FindHotQueriesAsync(FindHotQueriesRequest request, CancellationToken ct = default) =>
        RunAsync("hot queries", async () =>
        {
            var source = await QueryHotQueriesAsync(request.Fetch, ct);
            var availability = new HotQuerySourceInfo(source.Name, source.WindowDays, source.Missing);
            if (source.Name is null) return HotQueryEngine.Build([], [], availability, 0, []);

            var tables = (await ResolveTableScopeAsync(null, null, ct, includePartitions: true)).Tables;
            var ranked = HotQueryEngine.Rank(source.Queries, [.. tables.Select(t => (t.Schema, t.Name))], Dialect, request);

            List<Finding> findings = [.. HotQueryEngine.StatisticsFindings(ranked, request)];
            return HotQueryEngine.Build(ranked, findings, availability, 0, []);
        });
}
