namespace DbLens.Core.Analysis.Migration;

public sealed record CheckMigrationSafetyResult(
    /// <summary>Each statement of the script, in order, with its recognized shape.</summary>
    IReadOnlyList<MigrationStatement> Statements,
    /// <summary>
    /// Findings derived from statement shape and catalog metadata. Contains exactly one
    /// migration-summary entry, always.
    /// </summary>
    IReadOnlyList<Finding> Findings,
    /// <summary>Heuristic results. Always empty for this tool; present so every result has one shape.</summary>
    IReadOnlyList<Finding> Signals,
    MigrationSummary Summary,
    IReadOnlyList<string> Caveats,
    bool Truncated,
    string Disclaimer
);

public sealed record MigrationSummary(
    int TotalFindings,
    /// <summary>Count of inferred signals. Always 0 for this tool.</summary>
    int TotalSignals,
    int StatementsAnalyzed,
    /// <summary>Statements whose shape was not recognized — no lock claim was made about them.</summary>
    int StatementsUnrecognized,
    /// <summary>Tables referenced by the script that were found in the catalog.</summary>
    int TablesResolved,
    /// <summary>Count per severity level. All five levels are always present.</summary>
    IReadOnlyDictionary<string, int> BySeverity,
    /// <summary>Count per rule ID. All rules are always present.</summary>
    IReadOnlyDictionary<string, int> ByRule
);
