namespace DbLens.Core.Analysis.Gdpr;

public sealed record ScanGdprResult(
    string Disclaimer,
    IReadOnlyList<Finding> Findings,
    GdprSummary Summary,
    IReadOnlyList<string> Caveats,
    bool Truncated);

public sealed record GdprSummary(
    int TotalFindings,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByRule,
    IReadOnlyDictionary<string, int> ByCategory);
