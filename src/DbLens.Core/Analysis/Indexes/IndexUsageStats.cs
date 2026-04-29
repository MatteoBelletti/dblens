namespace DbLens.Core.Analysis.Indexes;

/// <summary>Index scan counts collected from DB statistics views, accumulated since last restart.</summary>
public sealed record IndexUsageStats(
    /// <summary>Scan count keyed by (schema, table, index_name). Absent key = index not tracked.</summary>
    IReadOnlyDictionary<(string Schema, string Table, string Index), long> ScanCounts,
    double UptimeDays
);
