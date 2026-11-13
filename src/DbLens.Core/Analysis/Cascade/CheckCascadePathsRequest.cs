namespace DbLens.Core.Analysis.Cascade;

public sealed record CheckCascadePathsRequest(
    /// <summary>
    /// Qualified starting table, e.g. "public.users" or "dbo.orders".
    /// When null, all tables that are the target of at least one ON DELETE CASCADE FK are analyzed.
    /// </summary>
    string? StartingTable = null,

    /// <summary>
    /// Maximum depth to explore. Default 10, hard-capped at 50.
    /// </summary>
    int MaxDepth = 10,

    /// <summary>
    /// Schema filter. When specified, only FKs within that schema are traversed.
    /// </summary>
    string? Schema = null,

    /// <summary>
    /// Maximum number of distinct cascade paths to enumerate before stopping.
    /// Path enumeration is combinatorial: a table with several CASCADE children, each with
    /// several of their own, produces the product of those branches, and MaxDepth alone does
    /// not bound it. Traversal stops as soon as this many paths exist, so the cost is paid in
    /// paths found rather than in graph size.
    /// </summary>
    int MaxPaths = 500
);
