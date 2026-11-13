using DbLens.Core.Analysis.Denormalization.Verification;
using DbLens.Core.Analysis.Verification;

namespace DbLens.Core.Analysis.Denormalization;

public sealed record AnalyzeDenormalizationResult(
    /// <summary>
    /// Findings established by measurement. Empty unless the request asked for verification:
    /// every rule in this tool starts as a naming or structural heuristic, and a signal only
    /// moves here once a read-only query has measured it.
    /// </summary>
    IReadOnlyList<Finding> Findings,
    /// <summary>Hypotheses inferred from column names and table structure. Verify before acting.</summary>
    IReadOnlyList<Finding> Signals,
    DenormalizationSummary Summary,
    /// <summary>Human-readable notes about heuristic limitations or truncation.</summary>
    IReadOnlyList<string> Caveats,
    /// <summary>True when results were truncated to the soft cap.</summary>
    bool Truncated,
    /// <summary>Permanent statement of what this analysis can and cannot see.</summary>
    string Disclaimer,
    /// <summary>
    /// Accounting for the verification pass. Null when verification was not requested, which
    /// distinguishes "nothing was verified" from "verification found nothing".
    /// </summary>
    VerificationSummary? Verification = null
);

public sealed record DenormalizationSummary(
    /// <summary>Count of measured findings. Zero unless verification ran and confirmed something.</summary>
    int TotalFindings,
    /// <summary>Count of inferred signals.</summary>
    int TotalSignals,
    int TablesAnalyzed,
    /// <summary>Count per severity across findings and signals. All five levels are always present.</summary>
    IReadOnlyDictionary<string, int> BySeverity,
    /// <summary>Count per rule across findings and signals. All five rules are always present.</summary>
    IReadOnlyDictionary<string, int> ByRule,
    /// <summary>Signals that verification turned into measured findings.</summary>
    int PromotedFromSignals = 0
);
