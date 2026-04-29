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
    /// <summary>Rule confidence in [0,1]. All current rules are deterministic (≥ 0.85).</summary>
    double Confidence,
    /// <summary>Fully qualified table name, e.g. "public.orders".</summary>
    string Table,
    /// <summary>Columns involved in the finding. Null when not column-specific.</summary>
    IReadOnlyList<string>? Columns,
    string Title,
    string Description,
    /// <summary>Suggested SQL to fix the issue. Null when no safe automated fix exists.</summary>
    string? SqlFix,
    /// <summary>Rule-specific extra data. Null when not applicable.</summary>
    IReadOnlyDictionary<string, object>? Metadata
);
