using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbLens.Mcp.Infrastructure;

/// <summary>
/// Base for every tool that talks to a database: resolves the inspector a call should use and
/// holds the descriptions of the parameters the tools share.
/// </summary>
public abstract class InspectorTool(IServiceProvider services, IDatabaseInspectorFactory factory)
{
    protected const string DialectHelp = "Database engine. Omit only when the server has a single one configured; always pass it with connectionString.";
    protected const string ConnectionStringHelp = "Omit for the server's default.";
    protected const string SchemaHelp = "Null = all user schemas.";
    protected const string TablesHelp = "Names without schema. Null = all in scope.";
    protected const string DefaultSchemaHelp = "For unqualified table names. Null = public / dbo.";

    /// <summary>
    /// A caller-supplied connection string gets an ad-hoc inspector; none, or an empty one, the
    /// configured inspector of the dialect, which is the only configured one when the call names none.
    /// </summary>
    protected IDatabaseInspector ResolveInspector(DatabaseDialect dialect, string? connectionString)
    {
        var database = Databases.Resolve(dialect, ToolCall.Has("dialect"), connectionString);
        var inspector = database.Source == TargetSource.Parameter
            ? factory.Create(database.Dialect, database.ConnectionString)
            : ((IKeyedServiceProvider)services).GetRequiredKeyedService<IDatabaseInspector>(database.Dialect);

        if (ToolCall.Current is { } call) call.Target = inspector.Describe(database.Source);
        return inspector;
    }

    // A host that registers none (a C# caller building a tool by hand) has nothing configured.
    private ConfiguredDatabases Databases =>
        services.GetService<ConfiguredDatabases>() ?? new ConfiguredDatabases(new ConfigurationBuilder().Build());
}
