namespace DbLens.Core.Analysis.Fragmentation;

public sealed record AnalyzeFragmentationRequest(
    /// <summary>Schema to analyze. Null = all user schemas.</summary>
    string? Schema = null,
    /// <summary>Table names to include (without schema prefix). Null = all tables in scope.</summary>
    string[]? Tables = null,
    /// <summary>Fragmentation percent at or above which a rebuild is recommended.</summary>
    double HighFragmentationThreshold = 30.0,
    /// <summary>Fragmentation percent at or above which a reorganize is recommended.</summary>
    double ModerateFragmentationThreshold = 10.0,
    /// <summary>Page density percent below which an index is reported as sparsely packed.</summary>
    double LowPageDensityThreshold = 60.0,
    /// <summary>
    /// Indexes smaller than this page count are ignored. Fragmentation on a small index is
    /// noise: the pages usually sit in a single extent and a rebuild gains nothing.
    /// </summary>
    long MinPageCount = 1000,
    /// <summary>
    /// Whether to collect page density. On SQL Server this raises the scan mode from
    /// LIMITED to SAMPLED, which reads leaf pages and costs meaningfully more on large indexes.
    /// </summary>
    bool IncludePageDensity = false,
    /// <summary>What one index's measurement may take before it is given up as not measured.</summary>
    int IndexTimeoutSeconds = 30,
    /// <summary>What all the measurements of a call may take together; the indexes left are named in a caveat.</summary>
    int TotalBudgetSeconds = 60,
    /// <summary>A database created or restored fewer days ago has freshly built indexes: its fragmentation says little of production.</summary>
    int RecentRestoreDays = 30
);
