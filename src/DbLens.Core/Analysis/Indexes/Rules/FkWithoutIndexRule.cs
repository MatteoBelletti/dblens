using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Indexes.Rules;

public static class FkWithoutIndexRule
{
    public static IEnumerable<Finding> Apply(
        IReadOnlyList<TableDetail> tables,
        DatabaseDialect dialect)
    {
        foreach (var table in tables)
        {
            foreach (var fk in table.ForeignKeysOutgoing)
            {
                if (IsCoveredByIndex(fk.FromColumns, table.Indexes))
                    continue;

                var colList = string.Join(", ", fk.FromColumns);
                var tableQualified = table.Qualified();
                var sqlFix = IndexSql.CreateIndex(table.Schema, table.Name, fk.FromColumns, dialect);

                yield return new Finding(
                    RuleId:      IndexRuleIds.FkWithoutIndex,
                    Severity:    Severity.High,
                    Category:    FindingCategory.Performance,
                    Confidence:  1.0,
                    Evidence:  Evidence.Derived,
                    Table:       tableQualified,
                    Columns:     fk.FromColumns,
                    Title:       $"Foreign key '{fk.ConstraintName}' has no covering index",
                    Description: $"FK column(s) {colList} on {tableQualified} " +
                                 $"(referencing {fk.ToSchema}.{fk.ToTable}) are not covered by any index. " +
                                 "JOINs and referential integrity checks require a sequential scan.",
                    SqlFix:      SqlProposal.Ddl(sqlFix, dialect == DatabaseDialect.PostgreSql
                        ? "Creates the covering index without blocking writes."
                        : "Creates the covering index online. ONLINE = ON needs Enterprise or Developer edition; " +
                          "on Standard remove it and run the build in a maintenance window."),
                    Metadata:    new Dictionary<string, object>
                    {
                        ["constraint_name"]  = fk.ConstraintName,
                        ["referenced_table"] = $"{fk.ToSchema}.{fk.ToTable}"
                    });
            }
        }
    }

    /// <summary>
    /// Returns true if fkCols are covered by any index — i.e., at least one index whose
    /// leading columns exactly match fkCols in order (a superset index also counts).
    /// </summary>
    private static bool IsCoveredByIndex(
        IReadOnlyList<string> fkCols,
        IReadOnlyList<IndexDetail> indexes)
    {
        foreach (var idx in indexes)
        {
            if (idx.Columns.Count < fkCols.Count) continue;
            var match = true;
            for (var i = 0; i < fkCols.Count; i++)
            {
                if (!string.Equals(idx.Columns[i], fkCols[i], StringComparison.OrdinalIgnoreCase))
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
        }
        return false;
    }
}
