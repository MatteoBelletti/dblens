using DbLens.Core.Analysis;

namespace DbLens.Core.Analysis.Indexes;

public sealed record AnalyzeIndexesResult(
    IReadOnlyList<Finding> Findings,
    AnalyzeIndexesSummary Summary,
    /// <summary>Human-readable notes about statistical limitations or truncation.</summary>
    IReadOnlyList<string> Caveats,
    /// <summary>True when findings were truncated to the soft cap (200 findings / 100 KB).</summary>
    bool Truncated
);

public sealed record AnalyzeIndexesSummary(
    int TotalFindings,
    /// <summary>Count per severity level. All five levels are always present.</summary>
    IReadOnlyDictionary<string, int> BySeverity,
    /// <summary>Count per rule ID. All four rules are always present.</summary>
    IReadOnlyDictionary<string, int> ByRule
);
