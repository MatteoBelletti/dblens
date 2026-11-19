using DbLens.Core.Analysis.Verification;

namespace DbLens.Core.Analysis.HotQueries;

public sealed record FindHotQueriesRequest(
    /// <summary>Measure each join that multiplies rows with an aggregate probe.</summary>
    bool VerifyJoins = false,
    /// <summary>Queries listed, heaviest first by total time.</summary>
    int Top = 20,
    /// <summary>Of those, how many get their plan read.</summary>
    int PlanTop = 10,
    /// <summary>Statistics rows read from the engine before own and catalog-only queries are set aside.</summary>
    int Fetch = 500,
    double ReadsPerRowThreshold = 100,
    double DiskReadShareThreshold = 0.2,
    /// <summary>A query must have run this often for its averages to mean anything.</summary>
    long MinCalls = 2,
    int MaxVerificationQueries = 20,
    int VerificationTimeoutSeconds = 10,
    int VerificationBudgetSeconds = 30,
    long MaxVerifiableTableRows = 5_000_000)
{
    public VerificationBudget Budget =>
        new(MaxVerificationQueries, VerificationTimeoutSeconds, VerificationBudgetSeconds, MaxVerifiableTableRows);
}

/// <summary>One query as the engine's statistics count it, before anything is derived from it.</summary>
public sealed record HotQueryStats(
    string QueryId,
    /// <summary>The text as the engine stored it. Never returned as is: see <see cref="SqlText.Redact"/>.</summary>
    string Text,
    long Calls,
    double TotalTimeMs,
    long Rows,
    /// <summary>8 KB pages read, from cache or not.</summary>
    long PagesRead,
    /// <summary>Pages read outside the database's cache.</summary>
    long PagesFromDisk,
    /// <summary>Pages written to temporary files or tempdb. Null when the source does not count them.</summary>
    long? TempPages,
    /// <summary>Where the dialect finds the query's stored plan (SQL Server). Null = none stored.</summary>
    string? PlanKey = null);

/// <summary>A query as returned: text redacted, figures per call.</summary>
public sealed record HotQuery(
    string QueryId,
    /// <summary>Literals replaced by "?", comments removed, at most 2,000 characters.</summary>
    string Text,
    long Calls,
    double TotalTimeMs,
    double MeanTimeMs,
    double RowsPerCall,
    /// <summary>Pages read per row returned; null when it returns none.</summary>
    double? PagesPerRow,
    /// <summary>Share of pages read outside the cache, 0 to 1.</summary>
    double DiskReadShare,
    long? TempPages,
    /// <summary>The user tables the text names, "schema.table".</summary>
    IReadOnlyList<string> Tables);

public sealed record FindHotQueriesResult(
    IReadOnlyList<HotQuery> Queries,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<Finding> Signals,
    HotQueriesSummary Summary,
    /// <summary>The source read, the window it covers, and what was missing.</summary>
    HotQuerySourceInfo Availability,
    IReadOnlyList<string> Caveats,
    bool Truncated,
    /// <summary>Null when verification was not requested.</summary>
    VerificationSummary? Verification = null);

public sealed record HotQuerySourceInfo(string? Source, double? WindowDays, IReadOnlyList<string> Missing);

public sealed record HotQueriesSummary(
    int TotalFindings,
    int TotalSignals,
    int QueriesListed,
    int PlansRead,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByRule);
