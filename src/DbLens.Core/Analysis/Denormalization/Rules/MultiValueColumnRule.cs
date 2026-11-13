using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Denormalization.Rules;

/// <summary>
/// Detects scalar text columns named like a collection (tags, role_list, product_ids).
/// A delimited list in one column cannot be indexed, joined or constrained by the database.
/// </summary>
public static class MultiValueColumnRule
{
    public static IEnumerable<Finding> Apply(IReadOnlyList<TableDetail> tables, DatabaseDialect dialect)
    {
        foreach (var table in tables)
        {
            var tableQualified = table.Qualified();

            foreach (var column in table.Columns)
            {
                // Native array and JSON types are a deliberate modelling choice, not a packed scalar.
                if (!ColumnNameVocabulary.IsTextType(column.DataType)) continue;
                if (!ColumnNameVocabulary.LooksLikeMultiValue(column.Name)) continue;

                yield return new Finding(
                    RuleId:     DenormalizationRuleIds.MultiValueColumn,
                    Severity:   Severity.Medium,
                    Category:   FindingCategory.Structure,
                    Confidence: 0.5,
                    Evidence: Evidence.Inferred,
                    Table:      tableQualified,
                    Columns:    [column.Name],
                    Title:      $"Column '{column.Name}' on '{tableQualified}' may pack multiple values into one scalar",
                    Description:
                        $"'{column.Name}' is a scalar {column.FullDataType} column with a collection-style name. " +
                        "If it stores a delimited list, the database cannot index individual values, enforce a " +
                        "foreign key on them, or join on them — every lookup degrades to a LIKE scan " +
                        "and membership tests match substrings by accident. " +
                        "Consider a join table, or a native array/JSON type with a supporting index. " +
                        "IMPORTANT: this rule reads the column name and type only — it does NOT inspect row data. " +
                        "Verify the actual contents before acting.",
                    SqlFix:     null,
                    Metadata:   new Dictionary<string, object>
                    {
                        ["dataType"] = column.FullDataType,
                        ["maxLength"] = column.CharacterMaxLength ?? -1,
                    });
            }
        }
    }

    /// <summary>
    /// Counts non-null values that contain a list delimiter. This is the cheapest probe in the
    /// set — one scan of one column — and the refutation direction is decisive: zero delimiters
    /// across tens of thousands of values settles it.
    ///
    /// The delimiter set is a hard-coded constant and must stay one: none of , ; | are LIKE
    /// metacharacters, so no escaping is needed, but a delimiter derived from schema text would
    /// reintroduce the injection problem quoting exists to remove.
    /// </summary>
    internal static string? BuildDelimiterProbe(
        TableDetail table, ColumnDetail column, DatabaseDialect dialect)
    {
        if (table.Kind != "table") return null;
        var target = SqlIdentifier.Qualify(table.Schema, table.Name, dialect);
        var col = SqlIdentifier.Quote(column.Name, dialect);

        return dialect == DatabaseDialect.PostgreSql
            ? $"""
               SELECT COUNT(*) AS compared_rows,
                      COUNT(*) FILTER (WHERE t.{col} LIKE '%,%' OR t.{col} LIKE '%;%' OR t.{col} LIKE '%|%') AS divergent_rows
               FROM {target} t
               WHERE t.{col} IS NOT NULL
               """
            : $"""
               SELECT COUNT_BIG(*) AS compared_rows,
                      COALESCE(SUM(CAST(CASE
                        WHEN t.{col} LIKE '%,%' OR t.{col} LIKE '%;%' OR t.{col} LIKE '%|%' THEN 1
                        ELSE 0 END AS BIGINT)), 0) AS divergent_rows
               FROM {target} t
               WHERE t.{col} IS NOT NULL
               """;
    }
}
