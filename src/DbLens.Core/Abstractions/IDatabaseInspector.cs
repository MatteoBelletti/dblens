namespace DbLens.Core.Abstractions;

/// <summary>
/// Contract for dialect-specific database inspection.
/// Implementations must be registered as keyed services using <see cref="DbLens.Core.Models.DatabaseDialect"/> as the key.
/// </summary>
public interface IDatabaseInspector
{
    /// <summary>The dialect this inspector targets.</summary>
    DbLens.Core.Models.DatabaseDialect Dialect { get; }

    /// <summary>Opens a connection and executes a trivial query to confirm reachability.</summary>
    Task<bool> TestConnectionAsync(CancellationToken ct = default);

    // Phase 2 — not implemented yet:
    // Task<IReadOnlyList<TableInfo>> GetTablesAsync(CancellationToken ct = default);
    // Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(string tableName, CancellationToken ct = default);
    // Task<QueryAnalysis> AnalyzeQueryAsync(string sql, CancellationToken ct = default);
}
