using System.ComponentModel;
using DbLens.Core.Analysis.Cascade;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class CheckCascadePathsTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "Traces ON DELETE CASCADE chains: what a DELETE on a table removes, and where the chain hits a cycle, " +
        "grows deep, switches to SET NULL or would be blocked by a NO ACTION/RESTRICT key. Useful for " +
        "right-to-erasure questions. " +
        "IMPORTANT: only declared foreign keys are analyzed; ORM cascades and triggers are invisible here. " +
        "Surface the Disclaimer. Do NOT infer cascade effects beyond what this tool returns.")]
    public Task<CheckCascadePathsResult> CheckCascadePaths(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("Qualified starting table, e.g. 'public.users'. Omit to analyze every cascade root.")]
        string? startingTable = null,
        [Description(SchemaHelp)]
        string? schema = null,
        [Description("Maximum cascade depth to explore. Default 10, hard cap 50.")]
        int maxDepth = 10,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        var inspector = ResolveInspector(dialect, connectionString);

        return inspector.CheckCascadePathsAsync(
            new CheckCascadePathsRequest(startingTable, maxDepth, schema),
            cancellationToken);
    }
}
