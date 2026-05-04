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
    string? Schema = null
);
