using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Denormalization.Rules;

/// <summary>
/// Detects numbered column families (phone1, phone2, phone3) — a repeating group,
/// which is a first-normal-form violation. The repeated values belong in a child table.
/// </summary>
public static class RepeatingGroupRule
{
    private const int MinGroupSize = 2;

    public static IEnumerable<Finding> Apply(IReadOnlyList<TableDetail> tables, DatabaseDialect dialect)
    {
        foreach (var table in tables)
        {
            var tableQualified = table.Qualified();

            var groups = table.Columns
                .Select(c => (Column: c, Parsed: TryParse(c)))
                .Where(x => x.Parsed is not null)
                .GroupBy(x => (x.Parsed!.Value.Stem, x.Column.DataType), CaseInsensitivePairComparer.Instance)
                .Where(g => g.Count() >= MinGroupSize);

            foreach (var group in groups)
            {
                var members = group
                    .OrderBy(x => x.Parsed!.Value.Ordinal)
                    .ToList();

                var columnNames = members.Select(m => m.Column.Name).ToList();
                var (stem, dataType) = group.Key;

                yield return new Finding(
                    RuleId:     DenormalizationRuleIds.RepeatingGroup,
                    Severity:   members.Count >= 3 ? Severity.Medium : Severity.Low,
                    Category:   FindingCategory.Structure,
                    // Two members can be a deliberate pattern (address_line1/2); three or more
                    // is almost always a repeating group that outgrew its column budget.
                    Confidence: members.Count >= 3 ? 0.9 : 0.6,
                    Evidence: Evidence.Inferred,
                    Table:      tableQualified,
                    Columns:    columnNames,
                    Title:      $"Repeating group '{stem}1..{members.Count}' on '{tableQualified}'",
                    Description:
                        $"Columns {string.Join(", ", columnNames.Select(c => $"'{c}'"))} on {tableQualified} " +
                        $"share the stem '{stem}' and the type '{dataType}'. " +
                        "A numbered column family is a repeating group (1NF violation): the number of values " +
                        "is capped by the schema, querying across them requires OR/UNION over every column, " +
                        "and adding one more value requires a migration. " +
                        "Consider moving these values into a child table keyed by the parent. " +
                        "Note: two-column families such as 'address_line1'/'address_line2' are sometimes a " +
                        "deliberate fixed-arity choice — review before changing.",
                    SqlFix:     null,
                    Metadata:   new Dictionary<string, object>
                    {
                        ["stem"] = stem,
                        ["memberCount"] = members.Count,
                        ["dataType"] = dataType,
                    });
            }
        }
    }

    private static (string Stem, int Ordinal)? TryParse(ColumnDetail column) =>
        ColumnNameVocabulary.TrySplitOrdinal(column.Name, out var stem, out var ordinal)
            ? (stem, ordinal)
            : null;

    /// <summary>
    /// Counts rows that fill every slot of the group. The 1NF violation is structural and no query
    /// refutes it; full rows show that the fixed number of slots is already a limit.
    /// </summary>
    internal static string? BuildOccupancyProbe(
        TableDetail table, IReadOnlyList<string> memberColumns, DatabaseDialect dialect)
    {
        const int maxMembers = 10;

        if (table.Kind != "table") return null;
        if (memberColumns.Count is 0 or > maxMembers) return null;
        var target = SqlIdentifier.Qualify(table.Schema, table.Name, dialect);
        var quoted = memberColumns.Select(c => $"t.{SqlIdentifier.Quote(c, dialect)}").ToList();
        var allFilled = string.Join(" AND ", quoted.Select(c => $"{c} IS NOT NULL"));

        return dialect == DatabaseDialect.PostgreSql
            ? $"""
               SELECT COUNT(*) AS compared_rows,
                      COUNT(*) FILTER (WHERE {allFilled}) AS divergent_rows
               FROM {target} t
               """
            : $"""
               SELECT COUNT_BIG(*) AS compared_rows,
                      COALESCE(SUM(CAST(CASE WHEN {allFilled} THEN 1 ELSE 0 END AS BIGINT)), 0) AS divergent_rows
               FROM {target} t
               """;
    }

}
