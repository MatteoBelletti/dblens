namespace DbLens.Core.Analysis.QueryPlan;

public sealed record ExplainQueryResult(
    /// <summary>
    /// True when the query was actually executed (Analyze). False when the plan holds
    /// optimizer estimates only — in that case row counts are predictions, not measurements.
    /// </summary>
    bool Executed,
    /// <summary>The plan as the engine rendered it, for human reading.</summary>
    string PlanText,
    /// <summary>Normalized plan tree. Null when the plan could not be parsed into nodes.</summary>
    QueryPlanNode? Root,
    /// <summary>
    /// Findings read from the plan the engine produced. Plan shape is factual; row counts are
    /// measurements when Executed is true and optimizer estimates otherwise.
    /// </summary>
    IReadOnlyList<Finding> Findings,
    /// <summary>Heuristic results. Always empty for this tool; present so every result has one shape.</summary>
    IReadOnlyList<Finding> Signals,
    QueryPlanSummary Summary,
    IReadOnlyList<string> Caveats,
    bool Truncated,
    string Disclaimer
);

public sealed record QueryPlanSummary(
    int TotalFindings,
    /// <summary>Count of inferred signals. Always 0 for this tool.</summary>
    int TotalSignals,
    int NodeCount,
    /// <summary>Optimizer cost of the root node, in engine-specific units.</summary>
    double? TotalEstimatedCost,
    /// <summary>Wall-clock time of the root node. Null unless the query was executed.</summary>
    double? TotalActualTimeMs,
    /// <summary>Count per severity level. All five levels are always present.</summary>
    IReadOnlyDictionary<string, int> BySeverity,
    /// <summary>Count per rule ID. All rules are always present.</summary>
    IReadOnlyDictionary<string, int> ByRule
);
