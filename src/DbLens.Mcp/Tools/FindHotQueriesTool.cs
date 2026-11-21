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
        "Finds the queries that weigh most, from the engine's own statistics (pg_stat_statements; Query Store, else the " +
        "plan cache): total time, pages read per row, reads outside the cache, spills, explain_query's plan rules on " +
        "the heaviest, and joins of one parent to two 1:N children that multiply rows (a signal; verifyJoins measures " +
        "it). IMPORTANT: text comes back with every literal replaced by ? and every comment removed; no query of the " +
        "user's is executed; " +
        "plans are estimated or stored. Without statistics, Availability says what each source needs: an empty list " +
        "then means nothing was measured, not that nothing is slow. Do NOT infer beyond the response.")]
    public Task<FindHotQueriesResult> FindHotQueries(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("Measure each join that multiplies rows with a read-only aggregate query: two counts, no row values.")]
        bool verifyJoins = false,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        [Description(ConnectionHelp)]
        string? connection = null,
        CancellationToken cancellationToken = default) =>
        ResolveInspector(dialect, connectionString, connection).FindHotQueriesAsync(new FindHotQueriesRequest(verifyJoins), cancellationToken);
}
