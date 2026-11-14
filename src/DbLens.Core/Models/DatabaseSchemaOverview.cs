namespace DbLens.Core.Models;

public sealed record DatabaseSchemaOverview(
    /// <summary>Written to be quoted as is, including what the response does not contain.</summary>
    string Summary,
    string Dialect,
    string Database,
    string GeneratedAt,
    IReadOnlyList<SchemaInfo> Schemas
);

public sealed record SchemaInfo(
    string Name,
    int TableCount,
    int ViewCount,
    IReadOnlyList<DatabaseObject> Objects
);

public sealed record DatabaseObject(
    string Name,
    DatabaseObjectKind Kind,
    /// <summary>From the engine's statistics. Null for a view, or a table never analyzed.</summary>
    long? ApproximateRowCount,
    long ColumnCount,
    bool HasPrimaryKey,
    long OutgoingForeignKeys,
    long IncomingForeignKeys,
    long IndexCount,
    /// <summary>Data, indexes and out-of-row storage.</summary>
    long TotalSizeBytes,
    string TotalSizeHuman,
    string? Comment,
    /// <summary>
    /// The partitioned table ("schema.name") this table is a partition of. Its rows and size are
    /// already counted there, and the structural diagnostics read that table, not this one.
    /// </summary>
    string? PartitionOf = null
);
