namespace DbLens.Core.Analysis.Migration;

public sealed record CheckMigrationSafetyRequest(
    /// <summary>The migration script. May contain several statements separated by semicolons.</summary>
    string Ddl,
    /// <summary>Default schema for statements whose table name has no schema prefix.</summary>
    string? DefaultSchema = null,
    /// <summary>
    /// Estimated row count above which a lock-taking operation is treated as high impact.
    /// Below this, the same operation completes fast enough that the lock is not the concern.
    /// </summary>
    long LargeTableRowThreshold = 1_000_000
);

/// <summary>Size and shape of a table targeted by the migration, read from the catalog.</summary>
public sealed record MigrationTableContext(
    string Schema,
    string Name,
    /// <summary>Approximate row count from database statistics. Null when never analyzed.</summary>
    long? ApproximateRowCount,
    long TotalSizeBytes,
    long IncomingForeignKeys
);
