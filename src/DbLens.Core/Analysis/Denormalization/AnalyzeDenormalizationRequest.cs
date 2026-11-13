using DbLens.Core.Analysis.Verification;

namespace DbLens.Core.Analysis.Denormalization;

public sealed record AnalyzeDenormalizationRequest(
    /// <summary>Schema to analyze. Null = all user schemas.</summary>
    string? Schema = null,
    /// <summary>Table names to include (without schema prefix). Null = all tables in scope.</summary>
    string[]? Tables = null,
    /// <summary>Column count above which a table is reported as a vertical-split candidate.</summary>
    int WideTableColumnThreshold = 40,

    /// <summary>
    /// When true, EXECUTES one read-only aggregate query per verifiable signal and promotes the
    /// measured ones into Findings. Default false: nothing is executed and the tool reads
    /// metadata only.
    /// </summary>
    bool VerifySignals = false,

    /// <summary>Hard cap on executed queries. Signals beyond it stay unverified.</summary>
    int MaxVerificationQueries = 20,

    /// <summary>Per-query timeout, enforced client-side and server-side.</summary>
    int VerificationTimeoutSeconds = 10,

    /// <summary>Wall-clock budget for the whole verification pass.</summary>
    int VerificationBudgetSeconds = 30,

    /// <summary>
    /// Tables with more approximate rows than this are not verified. A probe is a full scan or
    /// join, and this is the ceiling on what one diagnostic call may cost the database.
    /// </summary>
    long MaxVerifiableTableRows = 5_000_000
)
{
    public VerificationBudget Budget =>
        new(MaxVerificationQueries, VerificationTimeoutSeconds, VerificationBudgetSeconds, MaxVerifiableTableRows);
}
