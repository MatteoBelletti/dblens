using System.Data.Common;
using DbLens.Core.Analysis;
using DbLens.Core.Analysis.EfCore;
using DbLens.Core.Analysis.Hygiene;
using DbLens.Core.Analysis.Verification;

namespace DbLens.Core.Inspection;

/// <summary>
/// Unused columns and audit dates. A dialect supplies the planner's column statistics and the update
/// counters; scope, the EF model, the rules and the verification loop are the base's.
/// </summary>
public abstract partial class DatabaseInspectorBase
{
    /// <summary>
    /// A command returning schema_name, table_name, column_name, all_null and single_value: the
    /// planner's view of each column, read from statistics only, never a value.
    /// </summary>
    protected abstract DbCommand ColumnStatisticsCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables);

    /// <summary>A command returning schema_name, table_name and updates: rows updated since the counters started.</summary>
    protected abstract DbCommand TableUpdatesCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables);

    public Task<AnalyzeTableHygieneResult> AnalyzeTableHygieneAsync(AnalyzeTableHygieneRequest request, CancellationToken ct = default) =>
        RunAsync("table hygiene", async () =>
        {
            var scope = await ResolveTableScopeAsync(request.Schema, request.Tables, ct);
            if (scope.Tables.Count == 0)
                return HygieneEngine.Build(0, [], WithCaveats([], scope.Caveat, request.ModelCaveat), null);

            var details = await GetTableDetailsAsync(scope.Tables, ct);
            List<string> caveats = [];

            await using var conn = await OpenAsync(ct);
            var statistics = (await ReadAllAsync(ColumnStatisticsCommand(conn, scope.Tables), r => new ColumnStatistics(
                    r.Text("schema_name"), r.Text("table_name"), r.Text("column_name"), r.Flag("all_null"), r.Flag("single_value")), ct))
                .DistinctBy(s => (s.Schema, s.Table, s.Column))
                .ToList();

            // A zero only means "never updated" when the counters have been counting long enough.
            var window = await ReadStatsWindowAsync(conn, ct);
            IReadOnlyDictionary<(string, string), long>? updates = null;
            if (window.Days >= request.UpdateCountersMinDays && window.AnyIndexScanned)
                updates = (await ReadAllAsync(TableUpdatesCommand(conn, scope.Tables), r => (
                        Key: (r.Text("schema_name"), r.Text("table_name")), Updates: r.Number("updates")), ct))
                    .ToDictionary(x => x.Key, x => x.Updates);
            else
                caveats.Add(
                    $"The update counters cover {Num.F0(window.Days)} day(s), {window.SinceText}: fewer than " +
                    $"{request.UpdateCountersMinDays}, or no workload has run, so whether a table is ever updated is not known. " +
                    "No table was taken as insert-only, and audit-updated-not-maintained was not checked.");

            var models = (request.Models ?? []).Select(m => EfModelMatcher.Match(m, details.Tables, DefaultSchema)).ToList();
            caveats.AddRange(models.SelectMany(m => m.Ambiguities));

            var rowCounts = scope.Tables.ToDictionary(t => (t.Schema, t.Name), t => t.ApproximateRowCount);
            var plan = HygieneEngine.Analyze(
                new HygieneEngine.Input(details.Tables, rowCounts, statistics, updates, models, Dialect), request);
            caveats.AddRange(plan.Caveats);

            List<Finding> findings = [.. plan.Findings];
            VerificationSummary? verification = null;

            if (!request.VerifySignals)
                findings.AddRange(plan.Checks.Where(c => c.IsSignal).Select(c => c.Finding));
            else
            {
                // Smaller tables first: the budget then covers as many checks as it can.
                var checks = plan.Checks.OrderBy(c => c.Probe.ApproximateRowCount ?? long.MaxValue).ToList();
                var measurements = await RunVerificationProbesAsync([.. checks.Select(c => c.Probe)], request.Budget, ct);
                var byTarget = measurements.ToDictionary(m => m.Target.Key, StringComparer.Ordinal);

                findings.AddRange(checks
                    .Select(c => byTarget.TryGetValue(c.Probe.Target.Key, out var m) ? HygieneEngine.Apply(c, m) : null)
                    .OfType<Finding>());
                caveats.AddRange(request.Budget.NotRunCaveats(measurements));
                if (measurements.Any(m => m.Status == VerificationStatus.Inconclusive))
                    caveats.Add("Checks with too few rows to compare are reported as not measured, never as refuted.");
                verification = VerificationSummary.From(measurements, attempted: true);
            }

            return HygieneEngine.Build(details.Tables.Count, findings,
                WithCaveats(caveats, [scope.Caveat, NotFoundCaveat(details), request.ModelCaveat]), verification);
        });
}
