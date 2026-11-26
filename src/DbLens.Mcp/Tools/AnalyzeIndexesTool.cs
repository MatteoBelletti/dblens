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
        "Finds foreign keys without a covering index, duplicate and prefix-redundant indexes, and non-unique indexes " +
        "with no scans, each with its CREATE/DROP INDEX statement, and foreign key columns whose type is not the " +
        "referenced key's, with an ALTER COLUMN that rewrites the table. IMPORTANT: reads metadata and scan counters, not the " +
        "workload; counters reset on restart or restore, so read the Caveats before proposing a DROP. Do NOT infer " +
        "beyond the response.")]
    public Task<AnalyzeIndexesResult> AnalyzeIndexes(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(SchemaHelp)]
        string? schema = null,
        [Description(TablesHelp)]
        string[]? tables = null,
        [Description("Run the unused-index rule, which reads scan statistics.")]
        bool includeUnusedIndexes = true,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        [Description(ConnectionHelp)]
        string? connection = null,
        [Description(DatabaseHelp)]
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        var inspector = ResolveInspector(dialect, connectionString, connection, database);

        return inspector.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(schema, tables, includeUnusedIndexes),
            cancellationToken);
    }
}
