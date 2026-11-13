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
        "Reports index fragmentation (rebuild at 30%, reorganize at 10%) and, optionally, low page density, " +
        "with the REINDEX / ALTER INDEX statement for each. " +
        "IMPORTANT: PostgreSQL needs the pgstattuple extension and covers btree indexes only; SQL Server needs " +
        "VIEW DATABASE STATE. When StatsAvailable is false, say fragmentation could not be measured — NOT that " +
        "the indexes are healthy. Indexes under 1000 pages are skipped. " +
        "This is one input to a maintenance decision, not a plan: fill factor, split history and access " +
        "patterns are not considered. " +
        "Do NOT infer figures for indexes not in the response.")]
    public Task<AnalyzeFragmentationResult> AnalyzeFragmentation(
        [Description("Database dialect: PostgreSql or SqlServer.")]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("Schema to analyze. Null = all user schemas.")]
        string? schema = null,
        [Description("Table names without schema prefix. Null = all tables in scope.")]
        string[]? tables = null,
        [Description(
            "Also collect page density. On SQL Server this reads leaf pages (SAMPLED) and costs more. Default false.")]
        bool includePageDensity = false,
        [Description("Connection string. Omit to use the server's default for the dialect.")]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        return ResolveInspector(dialect, connectionString).AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(schema, tables, IncludePageDensity: includePageDensity),
            cancellationToken);
    }
}
