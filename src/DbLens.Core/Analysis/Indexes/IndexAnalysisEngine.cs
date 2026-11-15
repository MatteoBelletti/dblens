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
            if (stats.UptimeDays < request.UnusedIndexMinDays)
            {
                caveats.Add(
                    $"Insufficient DB uptime for the 'unused-index' rule " +
                    $"({Num.F0(stats.UptimeDays)} day(s), minimum required: {request.UnusedIndexMinDays}). " +
                    "The rule was skipped.");
            }
            else
            {
                var unusedFindings = UnusedIndexRule.Apply(tables, stats, dialect).ToList();
                findings.AddRange(unusedFindings);
                if (unusedFindings.Count > 0)
                    caveats.Add(
                        $"Database uptime: {Num.F0(stats.UptimeDays)} day(s). " +
                        "The 'unused-index' rule counts scans accumulated since the last DB restart. " +
                        "Verify manually before dropping any index.");
            }
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
