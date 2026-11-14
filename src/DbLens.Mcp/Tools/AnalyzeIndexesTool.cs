using System.ComponentModel;
using DbLens.Core.Analysis.Indexes;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class AnalyzeIndexesTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "Finds foreign keys without a covering index, duplicate and redundant (prefix) indexes, and non-unique " +
        "indexes with zero scans, each with the CREATE/DROP INDEX statement. " +
        "IMPORTANT: this reads metadata and scan counters, not query workloads. Scan counters reset on restart: " +
        "read the Caveats before recommending that an index be dropped. " +
        "Do NOT infer findings beyond what this tool returns.")]
    public Task<AnalyzeIndexesResult> AnalyzeIndexes(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(SchemaHelp)]
        string? schema = null,
        [Description(TablesHelp)]
        string[]? tables = null,
        [Description("Run the unused-index rule, which reads scan statistics. Default true.")]
        bool includeUnusedIndexes = true,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        var inspector = ResolveInspector(dialect, connectionString);

        return inspector.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(schema, tables, includeUnusedIndexes),
            cancellationToken);
    }
}
