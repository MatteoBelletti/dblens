using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DbLens.Mcp.Infrastructure;

/// <summary>
/// Base for every tool that talks to a database: resolves the inspector a call should use and
/// holds the descriptions of the parameters the tools share.
/// </summary>
public abstract class InspectorTool(IServiceProvider services, IDatabaseInspectorFactory factory)
{
    protected const string DialectHelp = "Database dialect: PostgreSql or SqlServer.";
    protected const string ConnectionStringHelp = "Connection string. Omit to use the server's default for the dialect.";
    protected const string SchemaHelp = "Schema to analyze. Null = all user schemas.";
    protected const string TablesHelp = "Table names without schema prefix. Null = all tables in scope.";
    protected const string DefaultSchemaHelp = "Schema for unqualified table names. Defaults to public / dbo.";

    /// <summary>
    /// A caller-supplied connection string gets an ad-hoc inspector; none, or an empty one, gets
    /// the configured inspector for the dialect.
    /// </summary>
    protected IDatabaseInspector ResolveInspector(DatabaseDialect dialect, string? connectionString) =>
        !string.IsNullOrWhiteSpace(connectionString)
            ? factory.Create(dialect, connectionString)
            : ((IKeyedServiceProvider)services).GetRequiredKeyedService<IDatabaseInspector>(dialect);
}
