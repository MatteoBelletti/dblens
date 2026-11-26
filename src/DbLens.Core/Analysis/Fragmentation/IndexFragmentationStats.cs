namespace DbLens.Core.Analysis.Fragmentation;

/// <summary>
/// Normalized physical index statistics, produced by the dialect-specific inspector and
/// consumed by <see cref="FragmentationAnalysisEngine"/>.
/// </summary>
public sealed record IndexFragmentationStats(
    IReadOnlyList<IndexFragmentation> Indexes,
    /// <summary>
    /// False when the engine could not read physical statistics at all — for example when the
    /// PostgreSQL 'pgstattuple' extension is not installed. Findings are suppressed and
    /// <see cref="UnavailableReason"/> explains why.
    /// </summary>
    bool StatsAvailable,
    string? UnavailableReason,
    /// <summary>How the numbers were obtained, surfaced to the caller as a caveat.</summary>
    string SourceDescription,
    /// <summary>Indexes above the size threshold whose measurement failed, timed out or did not fit the time budget.</summary>
    IReadOnlyList<NotMeasuredIndex>? NotMeasured = null
);

public sealed record NotMeasuredIndex(string Schema, string Table, string IndexName, string Reason);

/// <summary>An index the catalog lists, not measured yet. The handles are the hook's own way back to it (an oid, an object and index id).</summary>
public sealed record FragmentationCandidate(
    string Schema, string Table, string IndexName, bool IsPrimary, string Method, long PageCount, long Handle, long Handle2);

public sealed record IndexFragmentation(
    string Schema,
    string Table,
    string IndexName,
    /// <summary>
    /// Logical fragmentation percent (0–100): SQL Server avg_fragmentation_in_percent,
    /// PostgreSQL pgstatindex.leaf_fragmentation.
    /// </summary>
    double FragmentationPercent,
    /// <summary>Index size in database pages (8 KB on both engines).</summary>
    long PageCount,
    /// <summary>
    /// Percent of page space actually used (0–100). Null when not collected —
    /// SQL Server LIMITED scan mode does not report it.
    /// </summary>
    double? PageDensityPercent,
    bool IsPrimary,
    /// <summary>Index method, e.g. "btree" (PostgreSQL) or "CLUSTERED" (SQL Server).</summary>
    string Method
);
