using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Gdpr;

public static class GdprAnalysisEngine
{
    public const string Disclaimer =
        "DISCLAIMER: This is automated PII detection based on column name pattern matching. " +
        "Results may contain false positives and false negatives. " +
        "This tool is not a substitute for a formal GDPR compliance review by qualified legal " +
        "and technical professionals. Verify all findings before taking action.";

    public static ScanGdprResult Analyze(
        IReadOnlyList<TableDetail> tables,
        ScanGdprRequest request,
        DatabaseDialect dialect)
    {
        var findings = new List<Finding>();
        var caveats = new List<string>();
        var matchedColumns = new List<(string Table, string Column)>();

        foreach (var table in tables)
        {
            var gap = GapAnalyzer.Analyze(table);
            var piiColumnCount = 0;
            var qualified = table.Qualified();

            foreach (var col in table.Columns)
            {
                var match = PiiClassifier.Classify(col.Name, col.DataType, request.MinConfidence);
                if (match is null)
                    continue;

                piiColumnCount++;
                matchedColumns.Add((qualified, col.Name));
                findings.Add(BuildPiiFinding(table.Schema, table.Name, col, match, gap));
            }

            if (piiColumnCount > 0)
                findings.AddRange(BuildGapFindings(table, gap, piiColumnCount));
        }

        var coverage = GdprCoverageAnalyzer.Analyze(tables, matchedColumns);
        var split = FindingSplit.Partition(findings);

        if (split.TruncationCaveat() is { } truncation)
            caveats.Add(truncation);

        if (request.MinConfidence > 0.5)
            caveats.Add(
                $"MinConfidence set to {Num.P0(request.MinConfidence)}. " +
                "Lower it to surface more potential PII columns.");

        // An empty result from a name-matching scan is the most misreadable thing this tool can
        // return, so it is always accompanied by an explicit statement of what it does not mean.
        if (split.Total == 0 && coverage.ColumnsScanned > 0)
            caveats.Add(GdprCoverageAnalyzer.BuildNoMatchCaveat(coverage));

        if (GdprCoverageAnalyzer.BuildCaveat(coverage) is { } coverageCaveat)
            caveats.Add(coverageCaveat);

        if (split.SignalCaveat() is { } signalCaveat)
            caveats.Add(signalCaveat);

        return Build(split, coverage, caveats);
    }

    private static ScanGdprResult Build(
        FindingSplit split, GdprCoverage coverage, IReadOnlyList<string> caveats) =>
        new(
            Disclaimer: Disclaimer,
            Findings: split.Findings,
            Signals: split.Signals,
            Summary: BuildSummary(split),
            Coverage: coverage,
            Caveats: caveats,
            Truncated: split.Truncated);

    private static Finding BuildPiiFinding(
        string schema, string tableName,
        ColumnDetail col, PiiMatch match,
        TableGap gap)
    {
        var (ruleId, baseSeverity) = MapCategoryToRule(match.Category);

        // Critical stays Critical; High drops to Medium when an encryption marker is present.
        var severity = baseSeverity == Severity.High && gap.HasEncryptionMarker
            ? Severity.Medium
            : baseSeverity;

        var table = $"{schema}.{tableName}";
        var encryptionNote = gap.HasEncryptionMarker
            ? " (encryption marker detected on this table — severity reduced)"
            : string.Empty;

        return new Finding(
            RuleId:      ruleId,
            Severity:    severity,
            Category:    FindingCategory.Compliance,
            Confidence:  match.Confidence,
            Evidence:  Evidence.Inferred,
            Table:       table,
            Columns:     [col.Name],
            Title:       $"PII column detected: {col.Name} ({match.Category})",
            Description: $"Column '{col.Name}' ({col.DataType}) " +
                         (match.Qualified ? "contains" : "matches") + $" PII pattern '{match.PatternName}' " +
                         $"(category: {match.Category}){encryptionNote}.",
            SqlFix:      null,
            Metadata:    new Dictionary<string, object>
            {
                ["piiCategory"] = match.Category.ToString(),
                ["patternName"] = match.PatternName,
                ["sqlType"]     = col.DataType
            });
    }

    private static IEnumerable<Finding> BuildGapFindings(
        TableDetail table, TableGap gap, int piiColumnCount)
    {
        var missing = new List<string>();
        if (!gap.HasSoftDelete)  missing.Add("soft-delete column (e.g. deleted_at / is_deleted)");
        if (!gap.HasCreatedAt)   missing.Add("created_at audit timestamp");
        if (!gap.HasUpdatedAt)   missing.Add("updated_at audit timestamp");

        if (missing.Count == 0)
            yield break;

        var qualified = table.Qualified();

        yield return new Finding(
            RuleId:      GdprRuleIds.MissingGdprControls,
            Severity:    Severity.Medium,
            Category:    FindingCategory.Compliance,
            Confidence:  1.0,
            Evidence:  Evidence.Inferred,
            Table:       qualified,
            Columns:     null,
            Title:       $"Table with PII lacks GDPR controls: {table.Name}",
            Description: $"Table '{qualified}' contains {piiColumnCount} PII column(s) but is missing: " +
                         string.Join(", ", missing) + ". " +
                         "Soft-delete supports the GDPR right to erasure; " +
                         "audit timestamps support data accountability obligations.",
            SqlFix:      null,
            Metadata:    new Dictionary<string, object>
            {
                ["hasSoftDelete"]  = gap.HasSoftDelete,
                ["hasCreatedAt"]   = gap.HasCreatedAt,
                ["hasUpdatedAt"]   = gap.HasUpdatedAt,
                ["piiColumnCount"] = piiColumnCount
            });
    }

    private static (string RuleId, Severity Severity) MapCategoryToRule(PiiCategory category) =>
        category switch
        {
            PiiCategory.DirectIdentifier => (GdprRuleIds.PiiIdentifier,      Severity.High),
            PiiCategory.NationalId       => (GdprRuleIds.PiiIdentifier,      Severity.Critical),
            PiiCategory.Contact          => (GdprRuleIds.PiiContact,         Severity.Medium),
            PiiCategory.OnlineId         => (GdprRuleIds.PiiContact,         Severity.Low),
            PiiCategory.Financial        => (GdprRuleIds.PiiFinancial,       Severity.Critical),
            PiiCategory.SpecialCategory  => (GdprRuleIds.PiiSpecialCategory, Severity.Critical),
            PiiCategory.Location         => (GdprRuleIds.PiiLocation,        Severity.Low),
            _                            => (GdprRuleIds.PiiIdentifier,      Severity.Medium)
        };

    private static GdprSummary BuildSummary(FindingSplit split)
    {
        var byCategory = Enum.GetValues<PiiCategory>()
            .ToDictionary(
                c => c.ToString(),
                c => split.All.Count(f =>
                    f.Metadata is not null &&
                    f.Metadata.TryGetValue("piiCategory", out var v) &&
                    v is string s && s == c.ToString()));

        return new GdprSummary(
            TotalFindings: split.Findings.Count,
            TotalSignals: split.Signals.Count,
            BySeverity: split.BySeverity(),
            ByRule: split.ByRule(GdprRuleIds.All),
            ByCategory: byCategory);
    }

    public static ScanGdprResult Empty() =>
        Build(
            FindingSplit.Empty,
            new GdprCoverage(0, 0, 0, 0, [], 1.0),
            []);
}
