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
        var findings = new List<Finding>();
        findings.AddRange(FkWithoutIndexRule.Apply(tables, dialect));
        findings.AddRange(DuplicateIndexRule.Apply(tables, dialect));
        findings.AddRange(RedundantIndexRule.Apply(tables, dialect));

        var caveats = new List<string>();

        if (request.IncludeUnusedIndexes && stats is not null)
        {
            if (stats.UptimeDays < request.UnusedIndexMinDays)
            {
                caveats.Add(
                    $"Insufficient DB uptime for the 'unused-index' rule " +
                    $"({stats.UptimeDays:F0} day(s), minimum required: {request.UnusedIndexMinDays}). " +
                    "The rule was skipped.");
            }
            else
            {
                var unusedFindings = UnusedIndexRule.Apply(tables, stats, dialect).ToList();
                findings.AddRange(unusedFindings);
                if (unusedFindings.Count > 0)
                    caveats.Add(
                        $"Database uptime: {stats.UptimeDays:F0} day(s). " +
                        "The 'unused-index' rule counts scans accumulated since the last DB restart. " +
                        "Verify manually before dropping any index.");
            }
        }

        var truncated = false;
        if (findings.Count > DiagnosticLimits.MaxFindingCount)
        {
            findings = findings.Take(DiagnosticLimits.MaxFindingCount).ToList();
            truncated = true;
            caveats.Add(
                $"Results truncated to {DiagnosticLimits.MaxFindingCount} findings. " +
                "Use the schema/tables filters for a more targeted analysis.");
        }

        return new AnalyzeIndexesResult(
            Findings: findings,
            Summary: BuildSummary(findings),
            Caveats: caveats,
            Truncated: truncated);
    }

    public static AnalyzeIndexesResult Empty() =>
        new(
            Findings: [],
            Summary: BuildSummary([]),
            Caveats: [],
            Truncated: false);

    private static AnalyzeIndexesSummary BuildSummary(IReadOnlyList<Finding> findings)
    {
        var bySeverity = Enum.GetValues<Severity>()
            .ToDictionary(s => s.ToString(), s => findings.Count(f => f.Severity == s));

        var byRule = IndexRuleIds.All
            .ToDictionary(r => r, r => findings.Count(f => f.RuleId == r));

        return new AnalyzeIndexesSummary(
            TotalFindings: findings.Count,
            BySeverity: bySeverity,
            ByRule: byRule);
    }
}
