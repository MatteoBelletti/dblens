using System.Data.Common;
using System.Diagnostics;
using DbLens.Core.Analysis.Denormalization;
using DbLens.Core.Analysis.Denormalization.Verification;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Analysis.Verification;
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
    /// Measures one probe per item, in <paramref name="priority"/> order then smaller tables first so
    /// the budget covers as many as it can, and applies each measurement to its item. Skipped and
    /// failed probes are measured too, as such.
    /// </summary>
    protected async Task<(IReadOnlyList<TResult> Results, VerificationSummary Summary)> VerifyEachAsync<T, TResult>(
        IReadOnlyList<T> items, Func<T, VerificationProbe> probeOf, Func<T, VerificationMeasurement, TResult> apply,
        VerificationBudget budget, string inconclusiveCaveat, List<string> caveats, CancellationToken ct,
        Func<T, int>? priority = null)
    {
        var ordered = items.Select(i => (Item: i, Probe: probeOf(i)))
            .OrderBy(p => priority?.Invoke(p.Item) ?? 0)
            .ThenBy(p => p.Probe.ApproximateRowCount ?? long.MaxValue)
            .ToList();
        var measurements = await RunVerificationProbesAsync([.. ordered.Select(p => p.Probe)], budget, ct);

        caveats.AddRange(budget.NotRunCaveats(measurements));
        if (measurements.Any(m => m.Status == VerificationStatus.Inconclusive)) caveats.Add(inconclusiveCaveat);

        return ([.. ordered.Select((p, i) => apply(p.Item, measurements[i]))], VerificationSummary.From(measurements, attempted: true));
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

        await using var conn = await OpenAsync(ct);

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

        try
        {
            // A transaction per probe: sharing one would let a single failed probe abort every
            // probe after it on PostgreSQL.
            return await InRolledBackTransactionAsync(conn, VerificationSetupSql(limits), budgetToken, async tx =>
            {
                await using var cmd = Command(conn, probe.Sql, tx);
                cmd.CommandTimeout = limits.QueryTimeoutSeconds;

                await using var reader = await cmd.ExecuteReaderAsync(budgetToken);
                var counted = await reader.ReadAsync(budgetToken);

                return VerificationMeasurement.Measured(
                    probe, counted ? ReadCount(reader, 0) : 0, counted ? ReadCount(reader, 1) : 0,
                    sw.Elapsed.TotalMilliseconds);
            });
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
    }

    // SQL Server's COUNT is int, PostgreSQL's bigint: GetInt64 alone throws on one of them.
    private static long ReadCount(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
}
