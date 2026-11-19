namespace DbLens.Core.Analysis.Verification;

/// <summary>What a probe's numerator counts, which decides how the result is phrased.</summary>
public enum VerificationProbeKind
{
    /// <summary>Parent rows whose stored count differs from a live count of child rows.</summary>
    CachedCountDivergence,
    /// <summary>Joined rows where two copies of the same column differ.</summary>
    CopiedValueDivergence,
    /// <summary>Non-null values containing a list delimiter.</summary>
    DelimiterPresence,
    /// <summary>Rows in which every slot of a numbered column family is filled.</summary>
    RepeatingGroupOccupancy,
    /// <summary>
    /// Non-null referencing values with no matching row in the referenced key. Unlike every other
    /// kind, the hypothesis here ("this column references that table") is confirmed by ZERO.
    /// </summary>
    InclusionDependency,
    /// <summary>Rows a join to two children returns, against the children's rows read separately.</summary>
    JoinFanOut,
    /// <summary>Rows that hold a value. The hypothesis ("never filled") is confirmed by ZERO.</summary>
    NoValue,
    /// <summary>Distinct values among the filled rows. Confirmed by exactly ONE.</summary>
    SingleValue,
    /// <summary>Rows matching a condition that should match none. Confirmed by any.</summary>
    RowsMatching,
}

/// <summary>
/// How much a verification pass may cost the database. Shared by every tool that verifies its own
/// signals, so the limits and the skip policy are defined once.
/// </summary>
public sealed record VerificationBudget(
    int MaxQueries,
    int QueryTimeoutSeconds,
    int TotalBudgetSeconds,
    long MaxTableRows)
{
    /// <summary>
    /// Whether a probe may run. A table whose statistics were never collected carries no size
    /// guarantee, and scanning something of unknown size is the one cost a pass cannot bound.
    /// </summary>
    public VerificationSkipReason Evaluate(VerificationProbe probe, int executedSoFar)
    {
        if (executedSoFar >= MaxQueries) return VerificationSkipReason.QueryBudgetExhausted;
        if (probe.ApproximateRowCount is not { } rows) return VerificationSkipReason.RowCountUnknown;
        return rows > MaxTableRows ? VerificationSkipReason.TableTooLarge : VerificationSkipReason.None;
    }

    /// <summary>
    /// One caveat per reason a probe failed or did not run. The limits are server defaults, not tool
    /// parameters, so the advice is what the caller can change: the scope, or the statistics.
    /// </summary>
    public IEnumerable<string> NotRunCaveats(IReadOnlyList<VerificationMeasurement> measurements)
    {
        var failed = measurements.Where(m => m.Status == VerificationStatus.Failed).ToList();
        if (failed.Count > 0)
            yield return
                $"{failed.Count} verification query/queries failed and their signals remain unverified " +
                "conjectures: " + string.Join("; ", failed.Select(f => f.FailureKind.ToString()).Distinct()) + ".";

        foreach (var group in measurements.Where(m => m.Status == VerificationStatus.NotAttempted).GroupBy(m => m.SkipReason))
            yield return $"{group.Count()} signal(s) were not verified: " + group.Key switch
            {
                VerificationSkipReason.RowCountUnknown =>
                    "the tables' sizes are unknown because statistics have never been collected. " +
                    "Run ANALYZE (PostgreSQL) or UPDATE STATISTICS (SQL Server) and re-run.",
                VerificationSkipReason.TableTooLarge =>
                    $"the tables exceed the verification size limit of {Num.N0(MaxTableRows)} rows.",
                VerificationSkipReason.QueryBudgetExhausted =>
                    $"the query budget ({MaxQueries} per call) ran out. Narrow the scope with the schema/tables filters.",
                VerificationSkipReason.TimeBudgetExhausted =>
                    $"the time budget ({TotalBudgetSeconds} s per call) ran out. Narrow the scope with the schema/tables filters.",
                _ => "the heuristic cannot be checked against data without guessing.",
            };
    }
}

/// <summary>Identifies the signal a probe belongs to. Unique within one analysis run.</summary>
public sealed record VerificationTarget(string RuleId, string Table, IReadOnlyList<string> Columns)
{
    public string Key => $"{RuleId}|{Table}|{string.Join(",", Columns)}";
}

/// <summary>
/// A single read-only aggregate query the inspector may execute, produced by the pure engine.
/// The SQL selects two counts and never a row value.
/// </summary>
public sealed record VerificationProbe(
    VerificationTarget Target,
    VerificationProbeKind Kind,
    /// <summary>A single pure SELECT, all identifiers quoted, no trailing semicolon.</summary>
    string Sql,
    /// <summary>One sentence stating exactly what the numerator counts.</summary>
    string MeasurementDescription,
    /// <summary>Approximate rows in the scanned table, for ordering and skipping. Null = unknown.</summary>
    long? ApproximateRowCount,
    /// <summary>Fewer rows compared than this settle nothing: Inconclusive, never Refuted.</summary>
    long MinimumCompared = 1);

/// <summary>Every probe names its second count divergent_rows: how dblens tells its own probes in a workload.</summary>
public static class ProbeColumns
{
    public static bool IsProbe(string sql) => sql.Contains("divergent_rows", StringComparison.OrdinalIgnoreCase);
}

public enum VerificationStatus
{
    /// <summary>No probe was built, or the budget ran out before this one.</summary>
    NotAttempted = 0,
    /// <summary>The query errored.</summary>
    Failed = 1,
    /// <summary>The query ran but compared nothing, so it settles nothing.</summary>
    Inconclusive = 2,
    /// <summary>Measured, and nothing diverges.</summary>
    Refuted = 3,
    /// <summary>Measured, and something diverges.</summary>
    Confirmed = 4,
}

public enum VerificationSkipReason
{
    None = 0,
    /// <summary>The heuristic cannot be checked without guessing.</summary>
    NotVerifiable,
    QueryBudgetExhausted,
    TimeBudgetExhausted,
    TableTooLarge,
    /// <summary>Statistics have never been collected, so the table's size is unknown.</summary>
    RowCountUnknown,
    VerificationDisabled,
}

public enum VerificationFailureKind
{
    Unknown = 0,
    PermissionDenied,
    Timeout,
    ObjectMissing,
    TypeNotComparable,
    Cancelled,
    /// <summary>The generated SQL was rejected by the statement guard — always an internal bug.</summary>
    GuardRejected,
}

/// <summary>What a probe returned, or why it did not run. Produced by the inspector.</summary>
public sealed record VerificationMeasurement(
    VerificationTarget Target,
    VerificationProbeKind Kind,
    VerificationStatus Status,
    /// <summary>Rows compared. Zero means the probe settles nothing.</summary>
    long? Denominator,
    /// <summary>Rows that diverge.</summary>
    long? Numerator,
    string MeasurementDescription,
    string Sql,
    VerificationSkipReason SkipReason,
    VerificationFailureKind FailureKind,
    /// <summary>Sanitized engine message. Null unless Status is Failed.</summary>
    string? Error,
    double ElapsedMs)
{
    public static VerificationMeasurement Measured(
        VerificationProbe probe, long denominator, long numerator, double elapsedMs) =>
        new(probe.Target, probe.Kind, StatusOf(probe, denominator, numerator),
            denominator, numerator, probe.MeasurementDescription, probe.Sql,
            VerificationSkipReason.None, VerificationFailureKind.Unknown, null, elapsedMs);

    /// <summary>
    /// Confirmed means the hypothesis held. For most kinds the hypothesis is "something diverges",
    /// confirmed by a non-zero count; for an inclusion dependency it is "every value resolves" and
    /// for a never-filled column "no row holds a value", both confirmed by zero. Fewer compared rows
    /// than the probe needs settle nothing either way.
    /// </summary>
    private static VerificationStatus StatusOf(VerificationProbe probe, long denominator, long numerator)
    {
        if (denominator < Math.Max(probe.MinimumCompared, 1)) return VerificationStatus.Inconclusive;

        var hypothesisHolds = probe.Kind switch
        {
            VerificationProbeKind.InclusionDependency or VerificationProbeKind.NoValue => numerator == 0,
            VerificationProbeKind.SingleValue => numerator == 1,
            // The join multiplies when it returns more rows than the separate reads.
            VerificationProbeKind.JoinFanOut => numerator > denominator,
            _ => numerator > 0,
        };
        return hypothesisHolds ? VerificationStatus.Confirmed : VerificationStatus.Refuted;
    }

    public static VerificationMeasurement Skipped(VerificationProbe probe, VerificationSkipReason reason) =>
        new(probe.Target, probe.Kind, VerificationStatus.NotAttempted, null, null,
            probe.MeasurementDescription, probe.Sql, reason,
            VerificationFailureKind.Unknown, null, 0);

    public static VerificationMeasurement Failed(
        VerificationProbe probe, VerificationFailureKind kind, string error, double elapsedMs) =>
        new(probe.Target, probe.Kind, VerificationStatus.Failed, null, null,
            probe.MeasurementDescription, probe.Sql, VerificationSkipReason.None,
            kind, Sanitize(error), elapsedMs);

    /// <summary>
    /// Engine messages are collapsed to one line and truncated. A SELECT error does not normally
    /// echo row values, but truncation bounds the damage if one ever does.
    /// </summary>
    private static string Sanitize(string message)
    {
        var flat = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }
}

/// <summary>The verification block attached to a finding's metadata.</summary>
/// <summary>How every promoted finding says it was measured.</summary>
public static class ProbeText
{
    public const string HowRead = "Read with a read-only aggregate query; no row values were returned.";
}

public sealed record VerificationOutcome(
    string Status,
    string Measurement,
    long? RowsCompared,
    long? RowsDiverging,
    double? DivergenceRatio,
    /// <summary>Why nothing was measured. Null when it was.</summary>
    string? NotVerifiedReason,
    /// <summary>The exact SQL that produced the numbers, so the result can be reproduced by hand.</summary>
    string Query)
{
    /// <summary>A copy of <paramref name="metadata"/> carrying the outcome of <paramref name="m"/> under "verification".</summary>
    public static IReadOnlyDictionary<string, object> Attach(
        IReadOnlyDictionary<string, object>? metadata, VerificationMeasurement m, double? ratio)
    {
        var copy = metadata is null ? [] : new Dictionary<string, object>(metadata);
        copy["verification"] = new VerificationOutcome(
            Status: m.Status.ToString(),
            Measurement: m.MeasurementDescription,
            RowsCompared: m.Denominator,
            RowsDiverging: m.Numerator,
            DivergenceRatio: ratio is null ? null : Math.Round(ratio.Value, 4),
            NotVerifiedReason: m.Status switch
            {
                VerificationStatus.Confirmed or VerificationStatus.Refuted => null,
                VerificationStatus.Failed => m.FailureKind.ToString(),
                VerificationStatus.Inconclusive => m.Denominator is > 0 ? "TooFewRowsCompared" : "NoRowsCompared",
                _ => m.SkipReason.ToString(),
            },
            Query: m.Sql);
        return copy;
    }
}

/// <summary>Accounting for the verification pass.</summary>
public sealed record VerificationSummary(
    bool Attempted,
    int SignalsEligible,
    int QueriesExecuted,
    int Confirmed,
    int Refuted,
    int Inconclusive,
    int Failed,
    int NotAttempted,
    /// <summary>Count per outcome. All keys always present, so no caller needs a null check.</summary>
    IReadOnlyDictionary<string, int> ByOutcome,
    double TotalQueryTimeMs)
{
    /// <summary>Nothing to measure: attempted when it was asked for, absent when it was not.</summary>
    public static VerificationSummary? None(bool requested) => requested ? From([], attempted: true) : null;

    public static VerificationSummary From(
        IReadOnlyList<VerificationMeasurement> measurements, bool attempted)
    {
        var byOutcome = Enum.GetValues<VerificationStatus>()
            .ToDictionary(s => s.ToString(), s => measurements.Count(m => m.Status == s));

        return new VerificationSummary(
            Attempted: attempted,
            SignalsEligible: measurements.Count,
            QueriesExecuted: measurements.Count(m =>
                m.Status is VerificationStatus.Confirmed or VerificationStatus.Refuted
                    or VerificationStatus.Inconclusive or VerificationStatus.Failed),
            Confirmed: measurements.Count(m => m.Status == VerificationStatus.Confirmed),
            Refuted: measurements.Count(m => m.Status == VerificationStatus.Refuted),
            Inconclusive: measurements.Count(m => m.Status == VerificationStatus.Inconclusive),
            Failed: measurements.Count(m => m.Status == VerificationStatus.Failed),
            NotAttempted: measurements.Count(m => m.Status == VerificationStatus.NotAttempted),
            ByOutcome: byOutcome,
            TotalQueryTimeMs: Math.Round(measurements.Sum(m => m.ElapsedMs), 2));
    }
}
