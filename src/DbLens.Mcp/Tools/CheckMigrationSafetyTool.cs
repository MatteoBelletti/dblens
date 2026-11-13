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
        "Checks a migration script BEFORE it runs for table rewrites, blocking locks, non-concurrent index " +
        "builds, constraint validation scans and destructive drops, weighted by the real size of each table. " +
        "IMPORTANT: the script is NEVER executed. Statements are matched by shape, not parsed: kind 'Unknown' " +
        "means NOT recognized, not safe — report it as needing manual review, and never call a migration safe " +
        "from an empty findings list alone. Lock behaviour varies by engine version; read the Caveats. " +
        "Do NOT infer statements, sizes or findings beyond the response.")]
    public Task<CheckMigrationSafetyResult> CheckMigrationSafety(
        [Description("The migration script; several statements allowed. Never executed.")]
        string ddl,
        [Description("Database dialect: PostgreSql or SqlServer.")]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("Schema for unqualified table names. Defaults to public / dbo.")]
        string? defaultSchema = null,
        [Description("Connection string. Omit to use the server's default for the dialect.")]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ddl))
            throw new ArgumentException("ddl must not be empty.");

        return ResolveInspector(dialect, connectionString).CheckMigrationSafetyAsync(
            new CheckMigrationSafetyRequest(ddl, defaultSchema), cancellationToken);
    }
}
