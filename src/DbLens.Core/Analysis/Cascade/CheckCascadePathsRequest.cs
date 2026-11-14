namespace DbLens.Core.Analysis.Cascade;

public sealed record CheckCascadePathsRequest(
    /// <summary>"schema.table". Null: every table that an ON DELETE CASCADE key points at.</summary>
    string? StartingTable = null,

    /// <summary>Hard-capped at 50.</summary>
    int MaxDepth = 10,

    string? Schema = null,

    /// <summary>
    /// Paths multiply at every branching table, which depth alone does not bound: enumeration
    /// stops when this many exist.
    /// </summary>
    int MaxPaths = 500
);
