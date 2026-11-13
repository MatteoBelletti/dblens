namespace DbLens.Core.Analysis;

/// <summary>
/// A single diagnostic finding produced by an analysis rule.
/// This is the public contract for all dblens diagnostic tools — do not change the shape
/// without an explicit product decision.
/// </summary>
public sealed record Finding(
    /// <summary>Rule identifier, e.g. "fk-without-index".</summary>
    string RuleId,
    Severity Severity,
    FindingCategory Category,
    /// <summary>
    /// Rule confidence in [0,1]. Read it together with <see cref="Evidence"/>: confidence
    /// grades how sure the rule is within its own class, and does not promote a heuristic
    /// into a fact.
    /// </summary>
    double Confidence,
    /// <summary>
    /// What this finding stands on. Required, with no default: classifying a rule is a
    /// decision every rule author has to make, and a default would let a heuristic be
    /// published in the factual Findings list by omission.
    /// </summary>
    Evidence Evidence,
    /// <summary>Fully qualified table name, e.g. "public.orders".</summary>
    string Table,
    /// <summary>Columns involved in the finding. Null when not column-specific.</summary>
    IReadOnlyList<string>? Columns,
    string Title,
    string Description,
    /// <summary>
    /// Suggested SQL, carrying its own kind. Null when the rule has nothing to propose.
    /// Only <see cref="SqlProposal.SafeToExecuteAutomatically"/> may be run without review —
    /// the other kinds change the database or are templates with invented names.
    /// </summary>
    SqlProposal? SqlFix,
    /// <summary>Rule-specific extra data. Null when not applicable.</summary>
    IReadOnlyDictionary<string, object>? Metadata
);
