using System.Data.Common;
using System.Diagnostics;
using DbLens.Core.Analysis.Denormalization;
using DbLens.Core.Analysis.Denormalization.Verification;
using DbLens.Core.Analysis.Verification;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Models;
using Microsoft.Extensions.Logging;

namespace DbLens.Core.Inspection;

/// <summary>
/// Executes denormalization probes. The loop is dialect-independent; a dialect supplies only the
/// statements that configure each probe's transaction and the mapping from its error codes.
/// </summary>
public abstract partial class DatabaseInspectorBase
{
    /// <summary>
    /// Statements run inside each probe's transaction before the probe itself — the read-only
    /// mode, isolation level and server-side timeout appropriate to the engine.
    /// </summary>
    protected abstract string VerificationSetupSql(VerificationBudget budget);

    /// <summary>Maps a driver error onto a typed failure. Must not throw.</summary>
    protected abstract VerificationFailureKind ClassifyVerificationError(DbException ex);

    /// <summary>Dialect-specific statements about how verification ran, appended to the result.</summary>
    protected virtual IReadOnlyList<string> VerificationCaveats => [];

    private async Task<AnalyzeDenormalizationResult> VerifyDenormalizationAsync(
        AnalyzeDenormalizationResult result,
        IReadOnlyList<TableDetail> tables,
        IReadOnlyList<ScopedTable> scope,
        AnalyzeDenormalizationRequest request,
        CancellationToken ct)
    {
        // The scope already carries row counts, so sizing the probes costs no extra round trip.
        var rowCounts = scope.ToDictionary(t => (t.Schema, t.Name), t => t.ApproximateRowCount);

        var plan = VerificationPlanBuilder.Build(result.Signals, tables, rowCounts, request, Dialect);
        if (plan.Count == 0)
            return result with { Verification = VerificationSummary.From([], attempted: true) };

        var measurements = await RunVerificationProbesAsync(plan, request.Budget, ct);
        var verified = DenormalizationVerifier.Apply(result, measurements, request);

        return VerificationCaveats.Count == 0
            ? verified
            : verified with { Caveats = [..verified.Caveats, ..VerificationCaveats] };
    }

    /// <summary>
    /// Runs each probe in its own transaction. Never throws for a probe failure: an analysis that
    /// could not verify itself is still a valid analysis, and losing it because one table was
    /// unreadable would be a poor trade.
    /// </summary>
    protected async Task<IReadOnlyList<VerificationMeasurement>> RunVerificationProbesAsync(
        IReadOnlyList<VerificationProbe> probes,
        VerificationBudget limits,
        CancellationToken ct)
    {
        var measurements = new List<VerificationMeasurement>(probes.Count);
        var executed = 0;

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(limits.TotalBudgetSeconds));

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        foreach (var probe in probes)
        {
            if (budget.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                measurements.Add(VerificationMeasurement.Skipped(probe, VerificationSkipReason.TimeBudgetExhausted));
                continue;
            }

            var eligibility = limits.Evaluate(probe, executed);
            if (eligibility != VerificationSkipReason.None)
            {
                measurements.Add(VerificationMeasurement.Skipped(probe, eligibility));
                continue;
            }

            // Defence in depth: this SQL is ours, so a rejection means dblens generated something
            // it should not have. On an engine without a read-only transaction mode this guard is
            // load-bearing rather than a second line.
            var guard = StatementGuard.IsReadOnlySelect(probe.Sql, Dialect);
            if (!guard.Allowed)
            {
                Log.LogError(
                    "Generated verification probe was rejected by the statement guard: {Reason}. SQL: {Sql}",
                    guard.RejectionReason, probe.Sql);

                measurements.Add(VerificationMeasurement.Failed(
                    probe, VerificationFailureKind.GuardRejected, guard.RejectionReason ?? "rejected", 0));
                continue;
            }

            executed++;
            measurements.Add(await RunOneProbeAsync(conn, probe, limits, budget.Token, ct));
        }

        return measurements;
    }

    private async Task<VerificationMeasurement> RunOneProbeAsync(
        DbConnection conn,
        VerificationProbe probe,
        VerificationBudget limits,
        CancellationToken budgetToken,
        CancellationToken callerToken)
    {
        var sw = Stopwatch.StartNew();

        // One transaction per probe, deliberately. Any error poisons a PostgreSQL transaction
        // ("current transaction is aborted, commands ignored until end of transaction block"), so
        // sharing one across probes would let a single permission failure silently kill every probe
        // after it. Do not "optimize" this into a shared transaction.
        await using var tx = await conn.BeginTransactionAsync(budgetToken);
        try
        {
            await using (var setup = conn.CreateCommand())
            {
                setup.Transaction = tx;
                setup.CommandText = VerificationSetupSql(limits);
                setup.CommandTimeout = QueryTimeoutSeconds;
                await setup.ExecuteNonQueryAsync(budgetToken);
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = probe.Sql;
            cmd.CommandTimeout = limits.QueryTimeoutSeconds;

            await using var reader = await cmd.ExecuteReaderAsync(budgetToken);
            if (!await reader.ReadAsync(budgetToken))
                return VerificationMeasurement.Measured(probe, 0, 0, sw.Elapsed.TotalMilliseconds);

            return VerificationMeasurement.Measured(
                probe, ReadCount(reader, 0), ReadCount(reader, 1), sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return VerificationMeasurement.Failed(
                probe, VerificationFailureKind.Timeout, "budget exhausted", sw.Elapsed.TotalMilliseconds);
        }
        catch (DbException ex)
        {
            return VerificationMeasurement.Failed(
                probe, ClassifyVerificationError(ex), ex.Message, sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            // A result shape the reader did not expect must cost one probe, not the analysis.
            return VerificationMeasurement.Failed(
                probe, VerificationFailureKind.Unknown, ex.Message, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            await tx.RollbackAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Reads a count whatever integer width the engine chose. SQL Server's COUNT returns int while
    /// PostgreSQL's returns bigint; reading either with GetInt64 alone throws on the other.
    /// </summary>
    private static long ReadCount(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
}
