namespace DbLens.Core.Models;

/// <summary>Request for <see cref="DbLens.Core.Abstractions.IDatabaseInspector.GetTableDetailsAsync"/>.</summary>
public sealed record TableDetailsRequest(
    /// <summary>Table names to inspect. Format: "schema.name" or "name" (uses DefaultSchema). 1..20 elements.</summary>
    string[] Tables,
    /// <summary>Default schema when table names have no schema prefix. Null means "public" for PostgreSQL, "dbo" for SQL Server.</summary>
    string? DefaultSchema = null
);

/// <summary>Top-level response from GetTableDetailsAsync.</summary>
public sealed record TableDetailsResult(
    string Summary,
    string Dialect,
    string Database,
    string GeneratedAt,
    bool Truncated,
    IReadOnlyList<string> NotFound,
    IReadOnlyList<TableDetail> Tables
);

/// <summary>Full detail for a single table or view.</summary>
public sealed record TableDetail(
    string Schema,
    string Name,
    /// <summary>"table" or "view"</summary>
    string Kind,
    string? Comment,
    IReadOnlyList<ColumnDetail> Columns,
    PrimaryKeyInfo? PrimaryKey,
    IReadOnlyList<IndexDetail> Indexes,
    IReadOnlyList<ForeignKeyDetail> ForeignKeysOutgoing,
    IReadOnlyList<ForeignKeyDetail> ForeignKeysIncoming,
    IReadOnlyList<CheckConstraintDetail> CheckConstraints,
    IReadOnlyList<UniqueConstraintDetail> UniqueConstraints
);

public sealed record ColumnDetail(
    string Name,
    int OrdinalPosition,
    /// <summary>Base type without modifiers, e.g. "text", "character varying", "numeric".</summary>
    string DataType,
    /// <summary>Full type with modifiers, e.g. "character varying(255)", "numeric(10,2)".</summary>
    string FullDataType,
    bool IsNullable,
    string? DefaultExpression,
    int? CharacterMaxLength,
    int? NumericPrecision,
    int? NumericScale,
    bool IsIdentity,
    string? Comment
);

public sealed record PrimaryKeyInfo(
    string Name,
    IReadOnlyList<string> Columns
);

public sealed record IndexDetail(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsUnique,
    bool IsPrimary,
    /// <summary>Index method: btree, hash, gin, gist (PG) or CLUSTERED, NONCLUSTERED (SQL Server).</summary>
    string Method,
    /// <summary>Reconstructed CREATE INDEX statement.</summary>
    string Definition,
    bool IsPartial,
    string? Predicate
);

/// <summary>Foreign key constraint. Used for both outgoing and incoming; all fields are always populated.</summary>
public sealed record ForeignKeyDetail(
    string ConstraintName,
    string FromSchema,
    string FromTable,
    IReadOnlyList<string> FromColumns,
    string ToSchema,
    string ToTable,
    IReadOnlyList<string> ToColumns,
    /// <summary>Normalized: "NO ACTION", "CASCADE", "SET NULL", "SET DEFAULT", "RESTRICT".</summary>
    string OnDelete,
    string OnUpdate
);

public sealed record CheckConstraintDetail(
    string Name,
    string Expression
);

public sealed record UniqueConstraintDetail(
    string Name,
    IReadOnlyList<string> Columns
);
