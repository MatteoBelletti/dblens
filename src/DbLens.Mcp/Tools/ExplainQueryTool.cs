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
        "Returns a query's execution plan and flags large scans, bad row estimates, expensive nested loops, " +
        "spills to disk and post-scan filters. " +
        "IMPORTANT: with analyze = false nothing runs and row counts are optimizer ESTIMATES. With analyze = true " +
        "the query IS EXECUTED, only if it is a single pure SELECT, inside a transaction that is always rolled " +
        "back; anything else is rejected before reaching the database. Explain writes with analyze = false, and " +
        "do NOT rephrase a rejected statement to get past the check. " +
        "Do NOT infer plan nodes, row counts or timings not in the response.")]
    public Task<ExplainQueryResult> ExplainQuery(
        [Description("Exactly one SQL statement.")]
        string sql,
        [Description("Database dialect: PostgreSql or SqlServer.")]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("When true, EXECUTES the SELECT for real row counts and timings. Default false.")]
        bool analyze = false,
        [Description("Connection string. Omit to use the server's default for the dialect.")]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("sql must not be empty.");

        return ResolveInspector(dialect, connectionString).ExplainQueryAsync(
            new ExplainQueryRequest(sql, analyze), cancellationToken);
    }
}
