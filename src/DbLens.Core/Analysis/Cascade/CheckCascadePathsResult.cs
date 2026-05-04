using DbLens.Core.Analysis;

namespace DbLens.Core.Analysis.Cascade;

public sealed record CheckCascadePathsResult(
    string Dialect,
    string Database,
    string GeneratedAt,
    IReadOnlyList<CascadePath> Paths,
    IReadOnlyList<Finding> Findings,
    CascadeSummary Summary,
    IReadOnlyList<string> Caveats,
    bool Truncated,
    string Disclaimer
);

public sealed record CascadeSummary(
    int TotalFindings,
    int TotalPaths,
    int MaxDepth,
    int TotalTablesInvolved,
    /// <summary>Count per severity level. All five levels are always present.</summary>
    IReadOnlyDictionary<string, int> BySeverity,
    /// <summary>Count per rule ID. All six cascade rules are always present.</summary>
    IReadOnlyDictionary<string, int> ByRule
);

/// <summary>
/// One linearized CASCADE chain from a root table to a leaf (or a break point).
/// Multi-branch trees produce one CascadePath per leaf.
/// </summary>
public sealed record CascadePath(
    /// <summary>Fully qualified root table, e.g. "public.users".</summary>
    string RootTable,
    IReadOnlyList<CascadeStep> Steps,
    int Depth,
    IReadOnlyList<string> LeafTables,
    bool IsCircular,
    /// <summary>
    /// Qualified table name where the chain breaks due to SET NULL / RESTRICT / NO ACTION.
    /// Null when the path terminates naturally (leaf table or depth cap).
    /// </summary>
    string? BrokenAt
);

/// <summary>One step in a cascade chain.</summary>
public sealed record CascadeStep(
    /// <summary>Parent table being deleted from.</summary>
    string FromTable,
    /// <summary>Child table affected by the cascade.</summary>
    string ToTable,
    string ViaConstraint,
    /// <summary>Normalized delete action: "CASCADE", "SET NULL", "RESTRICT", "NO ACTION", "SET DEFAULT".</summary>
    string OnDelete,
    /// <summary>Approximate row count of ToTable from cached statistics. Null when stats are unavailable.</summary>
    long? EstimatedRowsAffected
);
