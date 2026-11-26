using System.ComponentModel;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class GetSchemaTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "Lists the user tables and views with row estimates, sizes and FK/index counts. IMPORTANT: column names are NOT " +
        "included, only ColumnCount: do NOT infer them, call get_table_details for the tables you need.")]
    public Task<DatabaseSchemaOverview> GetSchema(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        [Description(ConnectionHelp)]
        string? connection = null,
        [Description(DatabaseHelp)]
        string? database = null,
        [Description("Exact schema name. Omit for all user schemas.")]
        string? schemaFilter = null,
        [Description("SQL LIKE pattern on object names, e.g. \"order%\".")]
        string? namePattern = null,
        [Description("Include views.")]
        bool includeViews = true,
        [Description("Include system schemas (pg_catalog, information_schema).")]
        bool includeSystemSchemas = false,
        CancellationToken cancellationToken = default)
    {
        var inspector = ResolveInspector(dialect, connectionString, connection, database);

        var request = new SchemaOverviewRequest(schemaFilter, namePattern, includeViews, includeSystemSchemas);
        return inspector.GetSchemaOverviewAsync(request, cancellationToken);
    }
}
