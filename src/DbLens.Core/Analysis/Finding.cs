namespace DbLens.Core.Analysis;

/// <summary>
/// One result of an analysis rule: the public contract of every diagnostic tool. Do not change
/// its shape without an explicit product decision.
/// </summary>
public sealed record Finding(
    /// <summary>Rule identifier, e.g. "fk-without-index".</summary>
    string RuleId,
    Severity Severity,
    FindingCategory Category,
    /// <summary>In [0,1], within the finding's evidence class: it never promotes a heuristic to a fact.</summary>
    double Confidence,
    /// <summary>Required, with no default: a heuristic must not reach Findings by omission.</summary>
    Evidence Evidence,
    /// <summary>"schema.table".</summary>
    string Table,
    /// <summary>Null when the finding is not about specific columns.</summary>
    IReadOnlyList<string>? Columns,
    string Title,
    string Description,
    SqlProposal? SqlFix,
    IReadOnlyDictionary<string, object>? Metadata
);
