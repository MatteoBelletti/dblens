namespace DbLens.Core.Analysis.Indexes;

public sealed record AnalyzeIndexesRequest(
    /// <summary>Schema to analyze. Null = all user schemas.</summary>
    string? Schema = null,
    /// <summary>Table names to include (without schema prefix). Null = all tables in scope.</summary>
    string[]? Tables = null,
    /// <summary>Whether to run the unused-index rule (requires DB stats query).</summary>
    bool IncludeUnusedIndexes = true,
    /// <summary>
    /// Minimum DB uptime in days required to emit unused-index findings.
    /// If uptime is below this threshold, the rule is skipped and a caveat is added.
    /// </summary>
    int UnusedIndexMinDays = 7
);
