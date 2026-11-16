using DbLens.Core.Analysis.Indexes.Rules;
using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Indexes;

/// <summary>
/// Pure in-memory index analysis. No database access — operates on table details
/// and optional usage statistics supplied by the caller (dialect-specific inspector).
/// </summary>
public static class IndexAnalysisEngine
{
    public static AnalyzeIndexesResult Analyze(
        IReadOnlyList<TableDetail> tables,
        IndexUsageStats? stats,
        AnalyzeIndexesRequest request,
        DatabaseDialect dialect)
    {
        List<Finding> findings = [];
        findings.AddRange(FkWithoutIndexRule.Apply(tables, dialect));
        findings.AddRange(DuplicateIndexRule.Apply(tables, dialect));
        findings.AddRange(RedundantIndexRule.Apply(tables, dialect));

        List<string> caveats = [];

        if (request.IncludeUnusedIndexes && stats is not null)
        {
            var window = stats.Window;
            var days = Num.F0(window.Days);

            if (window.Days < request.UnusedIndexMinDays)
            {
                caveats.Add(
                    $"The scan counters cover only {days} day(s), {window.SinceText}; the 'unused-index' " +
                    $"rule needs {request.UnusedIndexMinDays}, so it was skipped.");
            }
            else if (!window.AnyIndexScanned)
            {
                caveats.Add(
                    $"No index of this database has recorded a single scan in {days} day(s) of counters: the workload " +
                    "has not run here (a freshly restored copy, for one), so every index would look unused. The " +
                    "'unused-index' rule was skipped.");
            }
            else
            {
                var unusedFindings = UnusedIndexRule.Apply(tables, stats, dialect).ToList();
                findings.AddRange(unusedFindings);
                if (unusedFindings.Count > 0)
                    caveats.Add(
                        $"The 'unused-index' rule read {days} day(s) of scan counters, {window.SinceText}. " +
                        "Verify manually before dropping any index.");
            }

            if (!window.RestoreVisible)
                caveats.Add(
                    "This login cannot see when the database was created or restored (on PostgreSQL that needs " +
                    "EXECUTE on pg_stat_file), so the scan counters are dated from the server start. If the " +
                    "database was restored later, they cover less time and 'unused-index' findings may be false.");
        }

        var split = FindingSplit.Partition(findings);

        if (split.TruncationCaveat() is { } truncation)
            caveats.Add(truncation);

        if (split.SignalCaveat() is { } signalCaveat)
            caveats.Add(signalCaveat);

        return Build(split, caveats);
    }

    public static AnalyzeIndexesResult Empty() => Build(FindingSplit.Empty, []);

    private static AnalyzeIndexesResult Build(FindingSplit split, IReadOnlyList<string> caveats) =>
        new(
            Findings: split.Findings,
            Signals: split.Signals,
            Summary: new AnalyzeIndexesSummary(
                TotalFindings: split.Findings.Count,
                TotalSignals: split.Signals.Count,
                BySeverity: split.BySeverity(),
                ByRule: split.ByRule(IndexRuleIds.All)),
            Caveats: caveats,
            Truncated: split.Truncated);
}
