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
        "DISABLED unless the server operator sets DbLens:AllowQueryResults; every other dblens tool returns no row " +
        "data. Answers a question with a figure and its evidence: runs one pure SELECT (anything else is rejected) in a " +
        "transaction always rolled back, returning at most 100 rows and a receipt: the exact SQL, the tables the plan " +
        "reads and, with crossCheckSql, whether an independent computation of the same figure (another join path, " +
        "detail against a stored total) agrees. IMPORTANT: the one tool that returns data values: select aggregates, " +
        "not personal data. Always show the receipt. If the cross-check DISAGREES report both figures, never pick one; " +
        "without one, say the figure is unconfirmed. Receipt.CertifiedAs: the user confirmed this query; prefer " +
        "certified queries when one fits. Do NOT state figures not in the result.")]
    public async Task<QueryWithReceiptResult> QueryWithReceipt(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("One SELECT statement. Omit when running a certified query by name.")]
        string? sql = null,
        [Description("Name of a certified query to run instead of sql.")]
        string? certified = null,
        [Description("A second SELECT that should produce the same result by another route.")]
        string? crossCheckSql = null,
        [Description("Relative difference allowed between cross-check numbers, e.g. 0.001. 0 = exact.")]
        double tolerance = 0.0,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        [Description(ConnectionHelp)]
        string? connection = null,
        CancellationToken cancellationToken = default)
    {
        if (!policy.AllowQueryResults)
            throw new ExecutionRefusedException(
                "query_with_receipt is disabled: dblens returns no row data unless the server operator sets " +
                "DbLens:AllowQueryResults=true (see docs/safety.md#query-results). Use explain_query or the " +
                "diagnostic tools instead.");

        var inspector = ResolveInspector(dialect, connectionString, connection);

        if (certified is not null)
            sql = (store.Find(inspector.Dialect, inspector.DatabaseName, certified)
                   ?? throw new ArgumentException($"No certified query named '{certified}' for this database.")).Sql;

        if (string.IsNullOrWhiteSpace(sql))
            throw new ArgumentException("Pass sql or the name of a certified query.");

        var result = await inspector.QueryWithReceiptAsync(
            new QueryWithReceiptRequest(sql, crossCheckSql, tolerance), cancellationToken);

        return store.Matching(inspector.Dialect, result.Receipt.Database, sql) is { } entry
            ? result with { Receipt = result.Receipt with { CertifiedAs = $"{entry.Name}: {entry.Question}" } }
            : result;
    }
}
