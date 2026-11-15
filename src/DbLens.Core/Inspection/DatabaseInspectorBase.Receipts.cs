using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Analysis.Receipts;
using Microsoft.Extensions.Logging;

namespace DbLens.Core.Inspection;

/// <summary>
/// query_with_receipt: runs a read-only query and returns its result together with the evidence a
/// reader needs to trust it — the SQL, the tables the plan reads, and an optional second route to
/// the same figure.
/// </summary>
public abstract partial class DatabaseInspectorBase
{
    /// <summary>
    /// Statements that make a transaction safe for a caller's SELECT: read-only where the engine has
    /// it, and a bounded lock wait. Unlike the verification setup this must not lower isolation —
    /// a figure someone is asked to trust cannot come from a dirty read.
    /// </summary>
    protected abstract string ReadOnlyQuerySetupSql { get; }

    public Task<QueryWithReceiptResult> QueryWithReceiptAsync(QueryWithReceiptRequest request, CancellationToken ct = default)
    {
        Guard(request.Sql, "query");
        if (request.CrossCheckSql is not null) Guard(request.CrossCheckSql, "cross-check query");
        if (request.Tolerance is not (>= 0 and <= 1))
            throw new ArgumentException("tolerance is a relative difference between 0 and 1, e.g. 0.001 for 0.1 %.");

        Log.LogInformation("Running query with receipt (length={Length}, crossCheck={CrossCheck})",
            request.Sql.Length, request.CrossCheckSql is not null);

        return RunAsync("query with receipt", async () =>
        {
            await EnsureReadOnlyLoginAsync(ct);

            var plan = await FetchQueryPlanAsync(new ExplainQueryRequest(request.Sql), ct);
            var tables = plan.Root?.DescendantsAndSelf()
                .Select(n => n.Relation).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];

            await using var conn = await OpenAsync(ct);

            var primary = await RunReadOnlyAsync(conn, request.Sql, request.MaxRows, ct);

            CrossCheck? crossCheck = null;
            if (request.CrossCheckSql is not null)
            {
                var check = await RunReadOnlyAsync(conn, request.CrossCheckSql, request.MaxRows, ct);
                var (status, explanation) = ResultComparer.Compare(primary, check, request.Tolerance);
                crossCheck = new CrossCheck(request.CrossCheckSql, status, explanation, check);
            }

            List<string> caveats = [];
            if (primary.Truncated)
                caveats.Add($"Only the first {request.MaxRows} rows are returned. Aggregate in SQL rather than reading rows.");
            if (crossCheck is null)
                caveats.Add("No cross-check was run: nothing independent confirms this figure.");
            else if (crossCheck.Status == CrossCheckStatus.Disagree)
                caveats.Add("The cross-check DISAGREES with the answer. Report both figures; do not pick one.");
            caveats.AddRange(ReceiptCaveats);

            return new QueryWithReceiptResult(
                primary,
                new Receipt(request.Sql, tables, conn.Database,
                    DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), crossCheck),
                caveats);
        });
    }

    /// <summary>Dialect statements about how far the read-only promise is engine-enforced.</summary>
    protected virtual IReadOnlyList<string> ReceiptCaveats => [];

    private void Guard(string sql, string what)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException($"The {what} must not be empty.");

        var guard = StatementGuard.IsReadOnlySelect(sql, Dialect);
        if (!guard.Allowed)
            throw new ArgumentException($"Refusing to run the {what}. {guard.RejectionReason}");
    }

    private Task<QueryResult> RunReadOnlyAsync(DbConnection conn, string sql, int maxRows, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        return InRolledBackTransactionAsync(conn, ReadOnlyQuerySetupSql, ct, async tx =>
        {
            await using var cmd = Command(conn, sql, tx);
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            List<IReadOnlyList<object?>> rows = [];
            var truncated = false;

            while (await reader.ReadAsync(ct))
            {
                if (rows.Count == maxRows) { truncated = true; break; }
                rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => ResultValue.Read(reader, i))]);
            }

            return new QueryResult(columns, rows, truncated, Math.Round(sw.Elapsed.TotalMilliseconds, 1));
        });
    }
}
