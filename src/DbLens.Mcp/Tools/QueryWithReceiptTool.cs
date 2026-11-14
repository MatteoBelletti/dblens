using System.ComponentModel;
using DbLens.Core.Analysis.Receipts;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class QueryWithReceiptTool(
    IServiceProvider services, IDatabaseInspectorFactory factory, CertifiedQueryStore store, ServerPolicy policy)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "DISABLED unless the server operator sets DbLens:AllowQueryResults — every other dblens tool returns no " +
        "row data. Answers a question with a figure and the evidence behind it. Runs one pure SELECT — anything else is " +
        "rejected — in a transaction that is always rolled back, and returns at most 100 rows plus a receipt: the " +
        "exact SQL, the tables the plan reads and, when crossCheckSql is given, whether an independent computation " +
        "of the same figure (another join path, or detail summed against a stored total) agrees. " +
        "IMPORTANT: this is the one tool that returns values from the data — select aggregates, not personal data. " +
        "Always show the receipt with the answer. If the cross-check DISAGREES, report both figures and say so; " +
        "never pick one. Without a cross-check, say the figure is unconfirmed. Receipt.CertifiedAs means the user " +
        "confirmed this query earlier; prefer certified queries when one fits. " +
        "Do NOT state figures that are not in the result.")]
    public async Task<QueryWithReceiptResult> QueryWithReceipt(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("One SELECT statement. Omit when running a certified query by name.")]
        string? sql = null,
        [Description("Name of a certified query to run instead of sql.")]
        string? certified = null,
        [Description("A second SELECT that should produce the same result by another route.")]
        string? crossCheckSql = null,
        [Description("Relative difference allowed between numbers in the cross-check, e.g. 0.001. Default 0: exact.")]
        double tolerance = 0.0,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        if (!policy.AllowQueryResults)
            throw new ExecutionRefusedException(
                "query_with_receipt is disabled: dblens returns no row data unless the server operator sets " +
                "DbLens:AllowQueryResults=true (see docs/safety.md#query-results). Use explain_query or the " +
                "diagnostic tools instead.");

        var inspector = ResolveInspector(dialect, connectionString);

        if (certified is not null)
            sql = (store.Find(dialect, inspector.DatabaseName, certified)
                   ?? throw new ArgumentException($"No certified query named '{certified}' for this database.")).Sql;

        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("Pass sql or the name of a certified query.");

        var result = await inspector.QueryWithReceiptAsync(
            new QueryWithReceiptRequest(sql, crossCheckSql, tolerance), cancellationToken);

        return store.Matching(dialect, result.Receipt.Database, sql) is { } entry
            ? result with { Receipt = result.Receipt with { CertifiedAs = $"{entry.Name}: {entry.Question}" } }
            : result;
    }
}
