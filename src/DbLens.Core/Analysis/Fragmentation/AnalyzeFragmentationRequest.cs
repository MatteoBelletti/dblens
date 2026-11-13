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
    bool IncludePageDensity = false
);
