using System.ComponentModel;

namespace DbLens.Core.Models;

/// <summary>Parameters for <see cref="DbLens.Core.Abstractions.IDatabaseInspector.GetSchemaOverviewAsync"/>.</summary>
public sealed record SchemaOverviewRequest(
    [property: Description("Exact schema name to filter by. null returns all user schemas.")]
    string? SchemaFilter = null,

    [property: Description("SQL LIKE pattern applied to object names (e.g. \"user%\"). null returns all.")]
    string? NamePattern = null,

    [property: Description("Whether to include views alongside tables. Defaults to true.")]
    bool IncludeViews = true,

    [property: Description("Whether to include system schemas (pg_catalog, information_schema, etc.). Defaults to false.")]
    bool IncludeSystemSchemas = false
);
