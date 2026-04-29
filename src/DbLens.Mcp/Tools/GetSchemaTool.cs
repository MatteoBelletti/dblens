using System.ComponentModel;
using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class GetSchemaTool(IServiceProvider services, IDatabaseInspectorFactory factory)
{
    // IServiceProvider is always injectable; cast to IKeyedServiceProvider at call time.
    private IKeyedServiceProvider KeyedServices => (IKeyedServiceProvider)services;

    [McpServerTool, Description(
        "Returns an overview of all user tables and views in a database, " +
        "with row count estimates, sizes, and FK/index counts. " +
        "Use this to explore an unfamiliar database. " +
        "For column-level details, use get_table_details.")]
    public Task<DatabaseSchemaOverview> GetSchema(
        [Description("Database dialect: PostgreSql or SqlServer.")]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(
            "Connection string to the target database. " +
            "PostgreSQL example: 'Host=myserver;Port=5432;Database=mydb;Username=user;Password=pass'. " +
            "SQL Server example: 'Server=myserver,1433;Database=mydb;User Id=user;Password=pass;TrustServerCertificate=true'. " +
            "If omitted, uses the server's configured default for the chosen dialect.")]
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
        var inspector = connectionString is not null
            ? factory.Create(dialect, connectionString)
            : KeyedServices.GetRequiredKeyedService<IDatabaseInspector>(dialect);

        var request = new SchemaOverviewRequest(schemaFilter, namePattern, includeViews, includeSystemSchemas);
        return inspector.GetSchemaOverviewAsync(request, cancellationToken);
    }
}
