using System.ComponentModel;
using DbLens.Core.Analysis.HotQueries;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class FindHotQueriesTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "Finds the queries that weigh most on the database, from the engine's own statistics " +
        "(pg_stat_statements; Query Store, else the plan cache): total time, pages read per row returned, " +
        "reads outside the cache, spills to temporary storage. " +
        "IMPORTANT: query text is returned with every literal replaced by ? and every comment removed; no query " +
        "of the user's is executed. When no statistics are available, Availability says what each source needs: " +
        "an empty list then means nothing was measured, not that nothing is slow. " +
        "Do NOT infer findings beyond what this tool returns.")]
    public Task<FindHotQueriesResult> FindHotQueries(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        CancellationToken cancellationToken = default) =>
        ResolveInspector(dialect, connectionString).FindHotQueriesAsync(new FindHotQueriesRequest(), cancellationToken);
}
