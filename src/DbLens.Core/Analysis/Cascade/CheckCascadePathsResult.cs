namespace DbLens.Core.Analysis.Cascade;

public sealed record CheckCascadePathsResult(
    string Dialect,
    string Database,
    string GeneratedAt,
    IReadOnlyList<CascadePath> Paths,
    /// <summary>Read from declared constraints, so facts. Always holds one cascade-summary.</summary>
    IReadOnlyList<Finding> Findings,
    /// <summary>Always empty for this tool; present so every result has one shape.</summary>
    IReadOnlyList<Finding> Signals,
    CascadeSummary Summary,
    IReadOnlyList<string> Caveats,
    bool Truncated,
    /// <summary>Path enumeration stopped at MaxPaths: the paths are a sample of the graph.</summary>
    bool PathsTruncated,
    string Disclaimer
);

public sealed record CascadeSummary(
    int TotalFindings,
    int TotalSignals,
    int TotalPaths,
    int MaxDepth,
    int TotalTablesInvolved,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByRule
);

/// <summary>One chain from a root table to a leaf or a break point; a tree yields one per leaf.</summary>
public sealed record CascadePath(
    string RootTable,
    IReadOnlyList<CascadeStep> Steps,
    int Depth,
    IReadOnlyList<string> LeafTables,
    bool IsCircular,
    /// <summary>Where SET NULL, RESTRICT or NO ACTION ends the chain. Null when it ends at a leaf or the depth cap.</summary>
    string? BrokenAt
);

public sealed record CascadeStep(
    /// <summary>The parent being deleted from.</summary>
    string FromTable,
    /// <summary>The child the delete reaches.</summary>
    string ToTable,
    string ViaConstraint,
    string OnDelete,
    /// <summary>Approximate rows of <see cref="ToTable"/>; null without statistics.</summary>
    long? EstimatedRowsAffected
);
