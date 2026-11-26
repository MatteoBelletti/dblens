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
    protected const string ConnectionHelp = "A connection saved under this name (see ping's connectionsFolder), instead of connectionString; it carries its own dialect.";
    protected const string DatabaseHelp = "Database on the configured server. Omit for the one in its connection string.";
    protected const string SchemaHelp = "Null = all user schemas.";
    protected const string TablesHelp = "Names without schema. Null = all in scope.";
    protected const string DefaultSchemaHelp = "For unqualified table names. Null = public / dbo.";

    /// <summary>
    /// A caller-supplied connection string gets an ad-hoc inspector; none, or an empty one, the
    /// configured inspector of the dialect, which is the only configured one when the call names none.
    /// </summary>
    protected IDatabaseInspector ResolveInspector(DatabaseDialect dialect, string? connectionString, string? connection = null, string? database = null)
    {
        var target = Resolve(dialect, connectionString, connection, database);
        var inspector = target.Source is TargetSource.Parameter or TargetSource.Named || !string.IsNullOrWhiteSpace(database)
            ? factory.Create(target.Dialect, target.ConnectionString)
            : ((IKeyedServiceProvider)services).GetRequiredKeyedService<IDatabaseInspector>(target.Dialect);

        if (ToolCall.Current is { } call)
            call.Target = inspector.Describe(target.Source) with { Connection = string.IsNullOrWhiteSpace(connection) ? null : connection };
        return inspector;
    }

    private ConfiguredDatabase Resolve(DatabaseDialect dialect, string? connectionString, string? connection, string? database)
    {
        if (!string.IsNullOrWhiteSpace(database))
        {
            if (!string.IsNullOrWhiteSpace(connectionString) || !string.IsNullOrWhiteSpace(connection))
                throw new ArgumentException("Pass database alone (it changes the database of the configured server), or connection or connectionString, not both.");

            var configured = Databases.Resolve(dialect, ToolCall.Has("dialect"), null);
            return configured with { ConnectionString = DatabaseInspectorFactory.WithDatabase(configured.Dialect, configured.ConnectionString, database.Trim()) };
        }

        if (string.IsNullOrWhiteSpace(connection))
            return Databases.Resolve(dialect, ToolCall.Has("dialect"), connectionString);

        if (!string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Pass connection or connectionString, not both.");

        var named = Connections.Resolve(connection.Trim());
        return ToolCall.Has("dialect") && dialect != named.Dialect && ToolCall.Current is not null
            ? throw new ArgumentException($"The connection '{connection}' is {named.Dialect}, not {dialect}: omit dialect.")
            : named;
    }

    protected NamedConnections Connections =>
        services.GetService<NamedConnections>() ?? new NamedConnections(new ConfigurationBuilder().Build());

    // A host that registers none (a C# caller building a tool by hand) has nothing configured.
    private ConfiguredDatabases Databases =>
        services.GetService<ConfiguredDatabases>() ?? new ConfiguredDatabases(new ConfigurationBuilder().Build());
}
