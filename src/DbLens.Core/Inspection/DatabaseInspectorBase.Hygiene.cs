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
    /// A command returning schema_name, table_name, column_name, all_null and single_value, one row per
    /// column: the planner's view of it, read from statistics only, never a value.
    /// </summary>
    protected abstract DbCommand ColumnStatisticsCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables);

    /// <summary>
    /// A command returning schema_name, table_name and updates: rows updated since the counters started,
    /// NULL when the engine's count is not reliable enough to read a zero as "never updated".
    /// </summary>
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
            var statistics = await ReadAllAsync(ColumnStatisticsCommand(conn, scope.Tables), r => new ColumnStatistics(
                r.Text("schema_name"), r.Text("table_name"), r.Text("column_name"), r.Flag("all_null"), r.Flag("single_value")), ct);
            var updates = await ReadTableUpdatesAsync(conn, scope.Tables, request, caveats, ct);

            var models = (request.Models ?? []).Select(m => EfModelMatcher.Match(m, details.Tables, DefaultSchema)).ToList();
            var unsupported = (request.Models ?? []).SelectMany(m => m.Unsupported).Distinct().ToList();
            caveats.AddRange(models.SelectMany(m => m.Ambiguities).Concat(unsupported));

            var rowCounts = scope.Tables.ToDictionary(t => (t.Schema, t.Name), t => t.ApproximateRowCount);
            var plan = HygieneEngine.Analyze(
                new HygieneEngine.Input(details.Tables, rowCounts, statistics, updates, models, Dialect, unsupported), request);
            caveats.AddRange(plan.Caveats);

            List<Finding> findings = [.. plan.Findings];
            VerificationSummary? verification = null;

            if (!request.VerifySignals)
                findings.AddRange(plan.Checks.Where(c => c.IsSignal).Select(c => c.Finding));
            else
            {
                var (measured, summary) = await VerifyEachAsync(plan.Checks, c => c.Probe, HygieneEngine.Apply, request.Budget,
                    "Checks with too few rows to compare are reported as not measured, never as refuted.", caveats, ct,
                    // A signal stands without its probe; a question is lost without it, so signals come first.
                    priority: c => c.IsSignal ? 0 : 1);
                findings.AddRange(measured.OfType<Finding>());
                verification = summary;
            }

            return HygieneEngine.Build(details.Tables.Count, findings,
                WithCaveats(caveats, [scope.Caveat, NotFoundCaveat(details), request.ModelCaveat]), verification);
        });

    /// <summary>
    /// Rows updated per table, or null when that is not known: the counters have not been counting
    /// long enough, or this login cannot read them. A zero means "never updated" only otherwise.
    /// </summary>
    private async Task<IReadOnlyDictionary<(string, string), long>?> ReadTableUpdatesAsync(
        DbConnection conn, IReadOnlyList<ScopedTable> tables, AnalyzeTableHygieneRequest request, List<string> caveats, CancellationToken ct)
    {
        const string Consequence = "so whether a table is ever updated is not known: no table was taken as insert-only, and " +
                                   "audit-updated-not-maintained was not checked.";
        try
        {
            var window = await ReadStatsWindowAsync(conn, ct);
            if (window.Days < request.UpdateCountersMinDays || !window.AnyIndexScanned)
            {
                caveats.Add($"The update counters cover {Num.F0(window.Days)} day(s), {window.SinceText}: fewer than " +
                            $"{request.UpdateCountersMinDays}, or no workload has run, {Consequence}");
                return null;
            }

            return (await ReadAllAsync(TableUpdatesCommand(conn, tables), r => (
                    Key: (r.Text("schema_name"), r.Text("table_name")), Updates: r.NumberOrNull("updates")), ct))
                .Where(x => x.Updates is not null)
                .ToDictionary(x => x.Key, x => x.Updates!.Value);
        }
        catch (DbException ex)
        {
            caveats.Add($"The update counters could not be read ({ex.Message.ReplaceLineEndings(" ").Trim()}), {Consequence}");
            return null;
        }
    }
}
