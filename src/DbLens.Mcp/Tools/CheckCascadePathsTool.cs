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
        "Traces ON DELETE CASCADE chains: what a DELETE on a table removes, and where a chain cycles, grows deep, turns " +
        "to SET NULL or is blocked by a NO ACTION/RESTRICT key. Useful for right-to-erasure questions. IMPORTANT: " +
        "declared foreign keys only; ORM cascades and triggers are invisible. Surface the Disclaimer. Do NOT infer " +
        "beyond the response.")]
    public Task<CheckCascadePathsResult> CheckCascadePaths(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("Qualified starting table, e.g. 'public.users'. Omit to analyze every cascade root.")]
        string? startingTable = null,
        [Description(SchemaHelp)]
        string? schema = null,
        [Description("Maximum cascade depth to explore; hard cap 50.")]
        int maxDepth = 10,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        [Description(ConnectionHelp)]
        string? connection = null,
        [Description(DatabaseHelp)]
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        var inspector = ResolveInspector(dialect, connectionString, connection, database);

        return inspector.CheckCascadePathsAsync(
            new CheckCascadePathsRequest(startingTable, maxDepth, schema),
            cancellationToken);
    }
}
