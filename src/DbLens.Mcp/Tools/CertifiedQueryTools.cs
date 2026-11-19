using System.ComponentModel;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class CertifiedQueryTools(
    IServiceProvider services, IDatabaseInspectorFactory factory, CertifiedQueryStore store)
    : InspectorTool(services, factory)
{
    public sealed record CertifyResult(CertifiedQueryStore.Entry Certified, string StoredIn);

    [McpServerTool, Description(
        "Saves a query the USER confirmed returns the right answer, to reuse it by name and cite it in receipts and the " +
        "glossary. It is checked by the statement guard and planned, never executed; the same name for this database is " +
        "replaced. IMPORTANT: call it only after the user has explicitly said the result is correct, never on your own " +
        "judgement.")]
    public async Task<CertifyResult> CertifyQuery(
        [Description("Short unique name, e.g. 'revenue_by_month'.")]
        string name,
        [Description("The question the query answers, in the user's words.")]
        string question,
        [Description("The SELECT statement the user confirmed.")]
        string sql,
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("name and question must not be empty.");

        var guard = StatementGuard.IsReadOnlySelect(sql, dialect);
        if (!guard.Allowed)
            throw new ArgumentException($"Only a read-only SELECT can be certified. {guard.RejectionReason}");

        var inspector = ResolveInspector(dialect, connectionString);
        await inspector.ExplainQueryAsync(new ExplainQueryRequest(sql), cancellationToken);

        return new CertifyResult(store.Save(name, question, sql, dialect, inspector.DatabaseName), store.Path);
    }

    [McpServerTool, Description(
        "Lists the queries the user certified for this database: name, question and SQL. Prefer one over new SQL when " +
        "it answers the question.")]
    public IReadOnlyList<CertifiedQueryStore.Entry> ListCertifiedQueries(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(ConnectionStringHelp)]
        string? connectionString = null) =>
        store.List(dialect, ResolveInspector(dialect, connectionString).DatabaseName);
}
