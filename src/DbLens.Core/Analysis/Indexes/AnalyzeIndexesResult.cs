using DbLens.Core.Analysis;

namespace DbLens.Core.Analysis.Indexes;

public sealed record AnalyzeIndexesResult(
    /// <summary>
    /// Findings established from catalog metadata or scan counters. Every rule in this tool is
    /// factual, so results appear here rather than in <see cref="Signals"/>.
    /// </summary>
    IReadOnlyList<Finding> Findings,
    /// <summary>Heuristic results. Always empty for this tool; present so every result has one shape.</summary>
    IReadOnlyList<Finding> Signals,
    AnalyzeIndexesSummary Summary,
    /// <summary>Human-readable notes about statistical limitations or truncation.</summary>
    IReadOnlyList<string> Caveats,
    /// <summary>True when results were truncated to the soft cap (200 entries).</summary>
    bool Truncated
);

public sealed record AnalyzeIndexesSummary(
    int TotalFindings,
    /// <summary>Count of inferred signals. Always 0 for this tool.</summary>
    int TotalSignals,
    /// <summary>Count per severity across findings and signals. All five levels are always present.</summary>
    IReadOnlyDictionary<string, int> BySeverity,
    /// <summary>Count per rule across findings and signals. All four rules are always present.</summary>
    IReadOnlyDictionary<string, int> ByRule
);
