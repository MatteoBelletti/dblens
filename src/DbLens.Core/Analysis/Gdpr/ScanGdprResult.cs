namespace DbLens.Core.Analysis.Gdpr;

public sealed record ScanGdprResult(
    string Disclaimer,
    /// <summary>
    /// Findings established from measurement or catalog guarantees. Every rule in this tool
    /// matches column names, so this list is always empty — the results are in
    /// <see cref="Signals"/>. It is present so the shape matches every other diagnostic tool.
    /// </summary>
    IReadOnlyList<Finding> Findings,
    /// <summary>
    /// Suspected PII, inferred from column names. A match means the name looks like personal
    /// data, not that the column holds any. Verify before acting.
    /// </summary>
    IReadOnlyList<Finding> Signals,
    GdprSummary Summary,
    /// <summary>What the scan could and could not see. Read this before interpreting an empty result.</summary>
    GdprCoverage Coverage,
    IReadOnlyList<string> Caveats,
    bool Truncated);

public sealed record GdprSummary(
    /// <summary>Count of established findings. Always 0 for this tool.</summary>
    int TotalFindings,
    /// <summary>Count of suspected PII signals.</summary>
    int TotalSignals,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByRule,
    IReadOnlyDictionary<string, int> ByCategory);
