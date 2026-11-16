namespace DbLens.Core.Analysis.EfCore;

/// <summary>
/// An EF Core model as its migrations snapshot describes it, read as text: no assembly of the
/// user's is ever loaded or run. Entities keep EF's own names; their tables are resolved.
/// </summary>
public sealed record EfModel(
    /// <summary>The DbContext the snapshot belongs to, e.g. "ShopContext".</summary>
    string ContextName,
    /// <summary>The EF Core version that wrote the snapshot ("10.0.15"). Null when absent.</summary>
    string? ProductVersion,
    /// <summary>The model's default schema (HasDefaultSchema). Null = the database default.</summary>
    string? DefaultSchema,
    IReadOnlyList<EfEntity> Entities,
    /// <summary>The migrations found beside the snapshot, oldest first.</summary>
    IReadOnlyList<string> MigrationIds,
    /// <summary>
    /// Mappings outside what dblens compares (TPT, TPC, JSON columns, views, keyless entities…),
    /// one sentence each naming the entity: reported, never silently skipped.
    /// </summary>
    IReadOnlyList<string> Unsupported);

/// <summary>An entity type and the table it is stored in.</summary>
public sealed record EfEntity(
    /// <summary>The CLR type name, e.g. "DemoShop.Order".</summary>
    string Name,
    /// <summary>The table's schema as configured; null = the model's default.</summary>
    string? Schema,
    string Table,
    IReadOnlyList<EfProperty> Properties,
    /// <summary>Primary key, as property names.</summary>
    IReadOnlyList<string> Key,
    IReadOnlyList<EfIndex> Indexes,
    IReadOnlyList<EfForeignKey> ForeignKeys,
    /// <summary>Set for an owned type stored in its owner's table, or a TPH subtype in its base's.</summary>
    string? SharesTableWith = null);

/// <summary>A property and the column it is stored in.</summary>
public sealed record EfProperty(
    string Name,
    /// <summary>As the snapshot writes it: "long", "string", "DateTimeOffset?".</summary>
    string ClrType,
    string Column,
    /// <summary>The store type the provider chose, e.g. "nvarchar(100)". Null when not written.</summary>
    string? ColumnType,
    bool IsNullable,
    int? MaxLength = null,
    bool? IsUnicode = null,
    int? Precision = null,
    int? Scale = null);

public sealed record EfIndex(
    /// <summary>Property names, in key order.</summary>
    IReadOnlyList<string> Properties,
    /// <summary>The name in the database when configured (HasDatabaseName); null = by convention.</summary>
    string? Name,
    bool IsUnique,
    string? Filter);

/// <summary>A relationship, on its dependent side.</summary>
public sealed record EfForeignKey(
    /// <summary>The principal entity's CLR type name.</summary>
    string Principal,
    IReadOnlyList<string> Properties,
    /// <summary>The principal properties referenced; empty = its primary key.</summary>
    IReadOnlyList<string> PrincipalKey,
    /// <summary>"Cascade", "Restrict", "SetNull", "NoAction", "ClientSetNull"…; null = EF's default.</summary>
    string? OnDelete);
