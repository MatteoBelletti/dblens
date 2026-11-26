using System.ComponentModel;
using System.Reflection;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class PingTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "Connects to the database a call would use and says which one it is: target (dialect, host, port, " +
        "database, login, whether it is on this machine, and where it came from: configured, parameter or demo) " +
        "and the database server's version, and the folder connections saved under a name are read from. Fails " +
        "with the reason when no database is configured or the " +
        "connection fails. IMPORTANT: no password is ever returned.")]
    public async Task<PingResult> Ping(
        [Description("Echoed back.")]
        string message = "pong",
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        [Description(ConnectionHelp)]
        string? connection = null,
        [Description(DatabaseHelp)]
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        var inspector = ResolveInspector(dialect, connectionString, connection, database);

        return new PingResult(
            Message: message,
            ServerVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            Timestamp: DateTime.UtcNow.ToString("O"),
            AvailableDialects: [nameof(DatabaseDialect.PostgreSql), nameof(DatabaseDialect.SqlServer)],
            DatabaseServerVersion: await inspector.GetServerVersionAsync(cancellationToken),
            ConnectionsFolder: Connections.Folder);
    }
}

/// <param name="ServerVersion">dblens's own version.</param>
/// <param name="DatabaseServerVersion">The database server's, as it reports it.</param>
/// <param name="ConnectionsFolder">Where connections saved under a name are read from (one &lt;name&gt;.conn each).</param>
public sealed record PingResult(
    string Message,
    string ServerVersion,
    string Timestamp,
    string[] AvailableDialects,
    string DatabaseServerVersion,
    string ConnectionsFolder);
