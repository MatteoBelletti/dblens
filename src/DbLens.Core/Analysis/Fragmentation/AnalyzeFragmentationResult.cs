namespace DbLens.Core.Analysis.Fragmentation;

public sealed record AnalyzeFragmentationResult(
    /// <summary>
    /// Findings read from physical measurements the engine performed. Every rule in this tool
    /// is a measurement, so results appear here rather than in <see cref="Signals"/>.
    /// </summary>
    IReadOnlyList<Finding> Findings,
    /// <summary>Heuristic results. Always empty for this tool; present so every result has one shape.</summary>
    IReadOnlyList<Finding> Signals,
    FragmentationSummary Summary,
    /// <summary>Notes about scan mode, skipped small indexes, or unavailable statistics.</summary>
    IReadOnlyList<string> Caveats,
    bool Truncated,
    /// <summary>
    /// False when physical statistics could not be read (for example: missing pgstattuple).
    /// When false, Findings contains no fragmentation results and Caveats explains why.
    /// </summary>
    bool StatsAvailable,
    string Disclaimer
);

public sealed record FragmentationSummary(
    int TotalFindings,
    /// <summary>Count of inferred signals. Always 0 for this tool.</summary>
    int TotalSignals,
    /// <summary>Indexes for which physical statistics were successfully read.</summary>
    int IndexesAnalyzed,
    /// <summary>Indexes skipped because they were below the MinPageCount threshold.</summary>
    int IndexesSkippedTooSmall,
    /// <summary>Highest fragmentation percent seen across analyzed indexes; 0 when none.</summary>
    double MaxFragmentationPercent,
    /// <summary>Count per severity level. All five levels are always present.</summary>
    IReadOnlyDictionary<string, int> BySeverity,
    /// <summary>Count per rule ID. All three rules are always present.</summary>
    IReadOnlyDictionary<string, int> ByRule
);
