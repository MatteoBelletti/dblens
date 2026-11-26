using System.ComponentModel;
using DbLens.Core.Analysis.Fragmentation;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class AnalyzeFragmentationTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "Reports index fragmentation (rebuild from 30%, reorganize from 10%) and optionally page density, with the " +
        "REINDEX / ALTER INDEX statement for each. IMPORTANT: PostgreSQL needs pgstattuple and covers btree only; SQL " +
        "Server needs VIEW DATABASE STATE. StatsAvailable false means not measured, NOT healthy; an index that took too " +
        "long or failed is named in the caveats, neither reported nor cleared. Indexes under 1,000 " +
        "pages are skipped. On a freshly restored database the numbers reflect the restore. One input to a maintenance decision, not a plan: fill factor, split history and access " +
        "patterns are not considered. Do NOT infer beyond the response.")]
    public Task<AnalyzeFragmentationResult> AnalyzeFragmentation(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(SchemaHelp)]
        string? schema = null,
        [Description(TablesHelp)]
        string[]? tables = null,
        [Description(
            "Also collect page density; on SQL Server it reads leaf pages (SAMPLED) and costs more.")]
        bool includePageDensity = false,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        [Description(ConnectionHelp)]
        string? connection = null,
        [Description(DatabaseHelp)]
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        return ResolveInspector(dialect, connectionString, connection, database).AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(schema, tables, IncludePageDensity: includePageDensity),
            cancellationToken);
    }
}
