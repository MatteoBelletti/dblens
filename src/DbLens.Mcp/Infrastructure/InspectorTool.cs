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
    protected const string DialectHelp = "Database engine.";
    protected const string ConnectionStringHelp = "Omit for the server's default.";
    protected const string SchemaHelp = "Null = all user schemas.";
    protected const string TablesHelp = "Names without schema. Null = all in scope.";
    protected const string DefaultSchemaHelp = "For unqualified table names. Null = public / dbo.";

    /// <summary>
    /// A caller-supplied connection string gets an ad-hoc inspector; none, or an empty one, gets
    /// the configured inspector for the dialect.
    /// </summary>
    protected IDatabaseInspector ResolveInspector(DatabaseDialect dialect, string? connectionString) =>
        !string.IsNullOrWhiteSpace(connectionString)
            ? factory.Create(dialect, connectionString)
            : ((IKeyedServiceProvider)services).GetRequiredKeyedService<IDatabaseInspector>(dialect);
}
