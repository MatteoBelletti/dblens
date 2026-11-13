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
        "Lists the user tables and views of a database with row estimates, sizes and FK/index counts. " +
        "IMPORTANT: column names are NOT included, only a ColumnCount — do NOT infer them; call " +
        "get_table_details for the tables you need.")]
    public Task<DatabaseSchemaOverview> GetSchema(
        [Description("Database dialect: PostgreSql or SqlServer.")]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("Connection string. Omit to use the server's default for the dialect.")]
        string? connectionString = null,
        [Description("Exact schema name to filter by. Omit to return all user schemas.")]
        string? schemaFilter = null,
        [Description("SQL LIKE pattern applied to object names, e.g. \"order%\". Omit to return all.")]
        string? namePattern = null,
        [Description("Whether to include views alongside tables. Defaults to true.")]
        bool includeViews = true,
        [Description("Whether to include system schemas such as pg_catalog and information_schema. Defaults to false.")]
        bool includeSystemSchemas = false,
        CancellationToken cancellationToken = default)
    {
        var inspector = ResolveInspector(dialect, connectionString);

        var request = new SchemaOverviewRequest(schemaFilter, namePattern, includeViews, includeSystemSchemas);
        return inspector.GetSchemaOverviewAsync(request, cancellationToken);
    }
}
