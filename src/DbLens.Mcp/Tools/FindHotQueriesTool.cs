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
        "reads outside the cache, spills to temporary storage, the plan rules of explain_query on the heaviest, and joins " +
        "of one parent to two 1:N children that multiply rows (a signal, measured with verifyJoins). " +
        "IMPORTANT: query text is returned with every literal replaced by ? and every comment removed; no query " +
        "of the user's is executed; plans are estimated or stored. When no statistics are available, Availability says what each source needs: " +
        "an empty list then means nothing was measured, not that nothing is slow. " +
        "Do NOT infer findings beyond what this tool returns.")]
    public Task<FindHotQueriesResult> FindHotQueries(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("Measure each join of one parent to two children with a read-only aggregate query: two counts, no row values. Default false.")]
        bool verifyJoins = false,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        CancellationToken cancellationToken = default) =>
        ResolveInspector(dialect, connectionString).FindHotQueriesAsync(new FindHotQueriesRequest(verifyJoins), cancellationToken);
}
