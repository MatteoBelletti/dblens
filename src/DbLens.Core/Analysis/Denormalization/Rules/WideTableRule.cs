using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Denormalization.Rules;

/// <summary>
/// Reports tables whose column count exceeds a threshold. A very wide table is often several
/// entities merged into one, or an entity that accumulated optional attributes over time.
/// </summary>
public static class WideTableRule
{
    public static IEnumerable<Finding> Apply(
        IReadOnlyList<TableDetail> tables,
        AnalyzeDenormalizationRequest request,
        DatabaseDialect dialect)
    {
        foreach (var table in tables)
        {
            if (table.Columns.Count <= request.WideTableColumnThreshold) continue;

            var tableQualified = table.Qualified();

            var nullableCount = table.Columns.Count(c => c.IsNullable);
            var nullableRatio = table.Columns.Count == 0
                ? 0
                : (double)nullableCount / table.Columns.Count;

            // A wide table that is also mostly nullable is the stronger signal: optional
            // attribute clusters that only apply to some rows belong in their own tables.
            var mostlyNullable = nullableRatio >= 0.7;

            yield return new Finding(
                RuleId:     DenormalizationRuleIds.WideTable,
                Severity:   mostlyNullable ? Severity.Low : Severity.Info,
                Category:   FindingCategory.Structure,
                Confidence: mostlyNullable ? 0.5 : 0.3,
                Evidence: Evidence.Inferred,
                Table:      tableQualified,
                Columns:    null,
                Title:      $"Table '{tableQualified}' has {table.Columns.Count} columns",
                Description:
                    $"{tableQualified} has {table.Columns.Count} columns, above the threshold of " +
                    $"{request.WideTableColumnThreshold}. {nullableCount} of them " +
                    $"({Num.P0(nullableRatio)}) are nullable. " +
                    (mostlyNullable
                        ? "A high proportion of nullable columns suggests optional attribute groups that apply " +
                          "to only some rows — candidates for extraction into their own tables."
                        : "This may be legitimate for a wide fact or reporting table.") +
                    " Wide tables increase row width, which costs I/O on every scan even when most columns " +
                    "are not selected.",
                SqlFix:     null,
                Metadata:   new Dictionary<string, object>
                {
                    ["columnCount"] = table.Columns.Count,
                    ["nullableCount"] = nullableCount,
                    ["threshold"] = request.WideTableColumnThreshold,
                });
        }
    }
}
