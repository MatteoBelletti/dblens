using System.ComponentModel;
using DbLens.Core.Abstractions;
using DbLens.Core.Analysis.Cascade;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class CheckCascadePathsTool(IServiceProvider services, IDatabaseInspectorFactory factory)
{
    private IKeyedServiceProvider KeyedServices => (IKeyedServiceProvider)services;

    [McpServerTool, Description(
        "Traces ON DELETE CASCADE chains from a starting table (or all cascade-root tables when unspecified). " +
        "Use when the user asks 'what happens if I delete from X', 'what cascades from Y', " +
        "wants to debug cascade behavior, or needs to verify GDPR right-to-erasure paths. " +
        "Returns structured cascade paths and diagnostic findings: " +
        "(1) cascade-circular-path — Critical: a cycle was detected in the CASCADE chain; " +
        "(2) cascade-deep-path — High: chain depth exceeds 5 levels; " +
        "(3) cascade-breaks-with-restrict — High: a NO ACTION/RESTRICT FK would block the DELETE at runtime (latent bug); " +
        "(4) cascade-breaks-with-set-null — Info: chain transitions to SET NULL (data retained, not deleted); " +
        "(5) cascade-self-referencing — Info: table has a self-referencing CASCADE (e.g. tree hierarchy); " +
        "(6) cascade-summary — Info: always present, summarizes total paths, max depth, tables involved. " +
        "The response always contains a Disclaimer field. Always surface it to the user. " +
        "IMPORTANT: Only schema-level FK constraints are analyzed. " +
        "Application-level deletions (ORM cascades, custom triggers) are NOT visible here. " +
        "Do NOT infer, guess, or generate cascade effects beyond what this tool returns.")]
    public Task<CheckCascadePathsResult> CheckCascadePaths(
        [Description("Database dialect: PostgreSql or SqlServer.")]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(
            "Qualified starting table, e.g. 'public.users' or 'dbo.orders'. " +
            "When omitted, all tables that are referenced by at least one ON DELETE CASCADE FK are analyzed.")]
        string? startingTable = null,
        [Description(
            "Schema to restrict the analysis to (e.g. 'public' for PostgreSQL, 'dbo' for SQL Server). " +
            "When omitted, all user schemas are included.")]
        string? schema = null,
        [Description(
            "Maximum cascade depth to explore. Default 10, hard cap 50. " +
            "Paths that exceed this depth are truncated and flagged with cascade-deep-path.")]
        int maxDepth = 10,
        [Description(
            "Connection string to the target database. " +
            "If omitted, uses the server's configured default for the chosen dialect.")]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        var inspector = connectionString is not null
            ? factory.Create(dialect, connectionString)
            : KeyedServices.GetRequiredKeyedService<IDatabaseInspector>(dialect);

        return inspector.CheckCascadePathsAsync(
            new CheckCascadePathsRequest(startingTable, maxDepth, schema),
            cancellationToken);
    }
}
