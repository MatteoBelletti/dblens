using System.ComponentModel;
using DbLens.Core.Analysis.Migration;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class CheckMigrationSafetyTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "Checks a migration script BEFORE it runs for table rewrites, blocking locks, non-concurrent index builds, " +
        "validation scans and destructive drops, weighted by each table's real size. IMPORTANT: the script is NEVER " +
        "executed. Statements are matched by shape: kind 'Unknown' means NOT recognized, not safe; report it for manual " +
        "review, and never call a migration safe from an empty list alone. Lock behaviour varies by engine version: " +
        "read the Caveats. Do NOT infer beyond the response.")]
    public Task<CheckMigrationSafetyResult> CheckMigrationSafety(
        [Description("The migration script; several statements allowed.")]
        string ddl,
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(DefaultSchemaHelp)]
        string? defaultSchema = null,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        [Description(ConnectionHelp)]
        string? connection = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ddl))
            throw new ArgumentException("ddl must not be empty.");

        return ResolveInspector(dialect, connectionString, connection).CheckMigrationSafetyAsync(
            new CheckMigrationSafetyRequest(ddl, defaultSchema), cancellationToken);
    }
}
