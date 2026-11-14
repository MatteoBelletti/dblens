namespace DbLens.Core.Models;

/// <summary>Top-level response from <see cref="DbLens.Core.Abstractions.IDatabaseInspector.GetSchemaOverviewAsync"/>.</summary>
public sealed record DatabaseSchemaOverview(
    /// <summary>Human-readable summary suitable for direct citation by an LLM.</summary>
    string Summary,
    /// <summary>Database dialect (e.g. "PostgreSql").</summary>
    string Dialect,
    /// <summary>Name of the connected database.</summary>
    string Database,
    /// <summary>ISO 8601 UTC timestamp of when this overview was generated.</summary>
    string GeneratedAt,
    /// <summary>All matching schemas with their objects, sorted alphabetically.</summary>
    IReadOnlyList<SchemaInfo> Schemas
);

/// <summary>All objects within a single database schema.</summary>
public sealed record SchemaInfo(
    /// <summary>Schema name.</summary>
    string Name,
    /// <summary>Number of tables in this schema.</summary>
    int TableCount,
    /// <summary>Number of views in this schema.</summary>
    int ViewCount,
    /// <summary>All objects sorted alphabetically by name.</summary>
    IReadOnlyList<DatabaseObject> Objects
);

/// <summary>High-level metadata for a single table or view.</summary>
public sealed record DatabaseObject(
    /// <summary>Object name.</summary>
    string Name,
    /// <summary>Whether this is a table or a view.</summary>
    DatabaseObjectKind Kind,
    /// <summary>Approximate row count from pg_class.reltuples. Null if the table has never been analyzed.</summary>
    long? ApproximateRowCount,
    /// <summary>Number of user-visible columns.</summary>
    long ColumnCount,
    /// <summary>Whether the object has a primary key constraint.</summary>
    bool HasPrimaryKey,
    /// <summary>Number of foreign key constraints where this object is the referencing table.</summary>
    long OutgoingForeignKeys,
    /// <summary>Number of foreign key constraints where this object is the referenced table.</summary>
    long IncomingForeignKeys,
    /// <summary>Total number of indexes (including those backing constraints).</summary>
    long IndexCount,
    /// <summary>Total size in bytes including indexes and TOAST tables.</summary>
    long TotalSizeBytes,
    /// <summary>Human-readable total size (e.g. "16 KB").</summary>
    string TotalSizeHuman,
    /// <summary>Object comment from pg_description, or null if not set.</summary>
    string? Comment,
    /// <summary>
    /// The partitioned table ("schema.name") this table is a partition of. Its rows and size are
    /// already counted in that table, and the structural diagnostics read the parent, not this.
    /// </summary>
    string? PartitionOf = null
);
