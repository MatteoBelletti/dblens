using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Gdpr;

public static class GdprAnalysisEngine
{
    internal const string Disclaimer =
        "DISCLAIMER: This is automated PII detection based on column name pattern matching. " +
        "Results may contain false positives and false negatives. " +
        "This tool is not a substitute for a formal GDPR compliance review by qualified legal " +
        "and technical professionals. Verify all findings before taking action.";

    private static readonly string[] AllRuleIds =
        ["pii-identifier", "pii-contact", "pii-financial", "pii-special-category", "pii-location", "missing-gdpr-controls"];

    public static ScanGdprResult Analyze(
        IReadOnlyList<TableDetail> tables,
        ScanGdprRequest request,
        DatabaseDialect dialect)
    {
        var findings = new List<Finding>();
        var caveats = new List<string>();

        foreach (var table in tables)
        {
            var gap = GapAnalyzer.Analyze(table);
            var piiColumns = new List<(ColumnDetail Column, PiiMatch Match)>();

            foreach (var col in table.Columns)
            {
                var match = PiiClassifier.Classify(col.Name, col.DataType, request.MinConfidence);
                if (match is null)
                    continue;

                piiColumns.Add((col, match));

                var finding = BuildPiiFinding(
                    table.Schema, table.Name, col, match, gap, dialect);
                findings.Add(finding);
            }

            if (piiColumns.Count > 0)
            {
                var gapFindings = BuildGapFindings(table, gap, piiColumns.Count, dialect);
                findings.AddRange(gapFindings);
            }
        }

        const int maxFindings = 200;
        var truncated = false;
        if (findings.Count > maxFindings)
        {
            findings = findings.Take(maxFindings).ToList();
            truncated = true;
            caveats.Add(
                $"Results truncated to {maxFindings} findings. " +
                "Use schema/tables filters for a more targeted scan.");
        }

        if (request.MinConfidence > 0.5)
            caveats.Add(
                $"MinConfidence set to {request.MinConfidence:P0}. " +
                "Lower it to surface more potential PII columns.");

        return new ScanGdprResult(
            Disclaimer: Disclaimer,
            Findings: findings,
            Summary: BuildSummary(findings),
            Caveats: caveats,
            Truncated: truncated);
    }

    private static Finding BuildPiiFinding(
        string schema, string tableName,
        ColumnDetail col, PiiMatch match,
        TableGap gap, DatabaseDialect dialect)
    {
        var (ruleId, baseSeverity) = MapCategoryToRule(match.Category);

        // Critical stays Critical; High drops to Medium when an encryption marker is present
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
            Table:       table,
            Columns:     [col.Name],
            Title:       $"PII column detected: {col.Name} ({match.Category})",
            Description: $"Column '{col.Name}' ({col.DataType}) matches PII pattern '{match.PatternName}' " +
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
        TableDetail table, TableGap gap, int piiColumnCount, DatabaseDialect dialect)
    {
        var qualified = $"{table.Schema}.{table.Name}";

        if (!gap.HasSoftDelete)
        {
            var missing = new List<string>();
            missing.Add("soft-delete column (e.g. deleted_at / is_deleted)");
            if (!gap.HasCreatedAt)      missing.Add("created_at audit timestamp");
            if (!gap.HasUpdatedAt)      missing.Add("updated_at audit timestamp");

            yield return new Finding(
                RuleId:      "missing-gdpr-controls",
                Severity:    Severity.Medium,
                Category:    FindingCategory.Compliance,
                Confidence:  1.0,
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
    }

    private static (string RuleId, Severity Severity) MapCategoryToRule(PiiCategory category) =>
        category switch
        {
            PiiCategory.DirectIdentifier => ("pii-identifier",       Severity.High),
            PiiCategory.NationalId       => ("pii-identifier",       Severity.Critical),
            PiiCategory.Contact          => ("pii-contact",          Severity.Medium),
            PiiCategory.OnlineId         => ("pii-contact",          Severity.Low),
            PiiCategory.Financial        => ("pii-financial",        Severity.Critical),
            PiiCategory.SpecialCategory  => ("pii-special-category", Severity.Critical),
            PiiCategory.Location         => ("pii-location",         Severity.Low),
            _                            => ("pii-identifier",       Severity.Medium)
        };

    private static GdprSummary BuildSummary(IReadOnlyList<Finding> findings)
    {
        var bySeverity = Enum.GetValues<Severity>()
            .ToDictionary(s => s.ToString(), s => findings.Count(f => f.Severity == s));

        var byRule = AllRuleIds
            .ToDictionary(r => r, r => findings.Count(f => f.RuleId == r));

        var byCategory = Enum.GetValues<PiiCategory>()
            .ToDictionary(
                c => c.ToString(),
                c => findings.Count(f =>
                    f.Metadata is not null &&
                    f.Metadata.TryGetValue("piiCategory", out var v) &&
                    v is string s && s == c.ToString()));

        return new GdprSummary(
            TotalFindings: findings.Count,
            BySeverity: bySeverity,
            ByRule: byRule,
            ByCategory: byCategory);
    }

    public static ScanGdprResult Empty() =>
        new(
            Disclaimer: Disclaimer,
            Findings: [],
            Summary: BuildSummary([]),
            Caveats: [],
            Truncated: false);
}
