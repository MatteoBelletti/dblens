using DbLens.Core.Analysis.Verification;

namespace DbLens.Core.Analysis.Relationships;

public sealed record InferRelationshipsRequest(
    /// <summary>Schema to analyze. Null = all user schemas.</summary>
    string? Schema = null,
    /// <summary>Table names to include (without schema prefix). Null = all tables in scope.</summary>
    string[]? Tables = null,
    /// <summary>
    /// When true, EXECUTES one read-only aggregate query per inferred relationship to test whether
    /// the referencing values actually exist in the referenced key. Default false: metadata only.
    /// </summary>
    bool VerifyCandidates = false,
    int MaxVerificationQueries = 20,
    int VerificationTimeoutSeconds = 10,
    int VerificationBudgetSeconds = 30,
    long MaxVerifiableTableRows = 5_000_000)
{
    public VerificationBudget Budget =>
        new(MaxVerificationQueries, VerificationTimeoutSeconds, VerificationBudgetSeconds, MaxVerifiableTableRows);
}

/// <summary>What a relationship in the map stands on.</summary>
public enum RelationshipEvidence
{
    /// <summary>A foreign key the schema declares.</summary>
    Declared,
    /// <summary>Suggested by naming and types only. Not checked against the data.</summary>
    Inferred,
    /// <summary>Checked: every non-null referencing value exists in the referenced key.</summary>
    Verified,
    /// <summary>Checked: most values resolve, but some reference nothing — a reference with orphans.</summary>
    VerifiedWithOrphans,
}

/// <summary>One join path between two tables.</summary>
public sealed record RelationshipEdge(
    string FromTable,
    IReadOnlyList<string> FromColumns,
    string ToTable,
    IReadOnlyList<string> ToColumns,
    RelationshipEvidence Evidence,
    /// <summary>The constraint name for a declared key; the naming basis for an inferred one.</summary>
    string Basis,
    /// <summary>Non-null referencing values compared. Null unless verified.</summary>
    long? RowsCompared = null,
    /// <summary>Referencing values with no match. Null unless verified.</summary>
    long? OrphanRows = null);

public sealed record InferRelationshipsResult(
    /// <summary>
    /// Every usable join path: declared keys, plus inferred ones that were not contradicted by the
    /// data. This is the relationship map; Findings and Signals explain the inferred entries.
    /// </summary>
    IReadOnlyList<RelationshipEdge> Relationships,
    /// <summary>Inferred relationships that a query measured. Empty unless verification ran.</summary>
    IReadOnlyList<Finding> Findings,
    /// <summary>Relationships suggested by names and types only.</summary>
    IReadOnlyList<Finding> Signals,
    RelationshipSummary Summary,
    IReadOnlyList<string> Caveats,
    bool Truncated,
    string Disclaimer,
    /// <summary>Null when verification was not requested.</summary>
    VerificationSummary? Verification = null);

public sealed record RelationshipSummary(
    int TotalFindings,
    int TotalSignals,
    int TablesAnalyzed,
    int DeclaredRelationships,
    int InferredRelationships,
    int VerifiedRelationships,
    /// <summary>Columns whose name matched more than one possible parent; no guess was made.</summary>
    int AmbiguousCandidatesSkipped,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByRule);
