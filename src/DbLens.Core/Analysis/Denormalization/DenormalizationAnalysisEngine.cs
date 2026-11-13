using DbLens.Core.Analysis.Denormalization.Rules;
using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Denormalization;

/// <summary>
/// Pure in-memory denormalization analysis. No database access — operates on table details
/// supplied by the caller (dialect-specific inspector).
/// </summary>
public static class DenormalizationAnalysisEngine
{
    public const string Disclaimer =
        "These rules read schema metadata only — column names, types, keys and foreign keys. " +
        "No row data is read, so every finding is a naming and structure heuristic, not a measurement. " +
        "A deliberate snapshot (a price captured at order time) is indistinguishable from an accidental " +
        "copy at the schema level. Confirm against the actual data before changing a schema.";

    public static AnalyzeDenormalizationResult Analyze(
        IReadOnlyList<TableDetail> tables,
        AnalyzeDenormalizationRequest request,
        DatabaseDialect dialect)
    {
        var findings = new List<Finding>();
        findings.AddRange(RepeatingGroupRule.Apply(tables, dialect));
        findings.AddRange(DerivedColumnRule.Apply(tables, dialect));
        findings.AddRange(DuplicatedColumnRule.Apply(tables, dialect));
        findings.AddRange(MultiValueColumnRule.Apply(tables, dialect));
        findings.AddRange(WideTableRule.Apply(tables, request, dialect));

        var caveats = new List<string>();

        var split = FindingSplit.Partition(findings);

        if (split.SignalCaveat() is { } signalCaveat)
            caveats.Add(signalCaveat);

        if (split.TruncationCaveat() is { } truncation)
            caveats.Add(truncation);

        return Build(split, caveats, tables.Count);
    }

    public static AnalyzeDenormalizationResult Empty() => Build(FindingSplit.Empty, [], 0);

    private static AnalyzeDenormalizationResult Build(
        FindingSplit split, IReadOnlyList<string> caveats, int tablesAnalyzed) =>
        new(
            Findings: split.Findings,
            Signals: split.Signals,
            Summary: new DenormalizationSummary(
                TotalFindings: split.Findings.Count,
                TotalSignals: split.Signals.Count,
                TablesAnalyzed: tablesAnalyzed,
                BySeverity: split.BySeverity(),
                ByRule: split.ByRule(DenormalizationRuleIds.All)),
            Caveats: caveats,
            Truncated: split.Truncated,
            Disclaimer: Disclaimer);
}
