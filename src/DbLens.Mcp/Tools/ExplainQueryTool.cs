using System.ComponentModel;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class ExplainQueryTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "Returns a query's execution plan and flags large scans, bad row estimates, costly nested loops, spills to disk " +
        "and post-scan filters. IMPORTANT: with analyze = false nothing runs and row counts are ESTIMATES. With analyze " +
        "= true the query IS EXECUTED, only if it is a single pure SELECT, in a transaction always rolled back; " +
        "anything else is rejected first. Explain writes with analyze = false, and do NOT rephrase a rejected statement " +
        "to pass the check. Do NOT infer beyond the response.")]
    public Task<ExplainQueryResult> ExplainQuery(
        [Description("Exactly one SQL statement.")]
        string sql,
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("When true, EXECUTES the SELECT for real row counts and timings.")]
        bool analyze = false,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("sql must not be empty.");

        return ResolveInspector(dialect, connectionString).ExplainQueryAsync(
            new ExplainQueryRequest(sql, analyze), cancellationToken);
    }
}
