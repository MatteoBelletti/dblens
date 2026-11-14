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
                if (IsCoveredByIndex(fk.FromColumns, table.Indexes) || IsCoveredByConstraint(fk.FromColumns, table))
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
    // SQL Server lists the index behind a UNIQUE constraint under the constraint, not under Indexes.
    private static bool IsCoveredByConstraint(IReadOnlyList<string> fkCols, TableDetail table) =>
        table.UniqueConstraints.Any(u =>
            u.Columns.Count >= fkCols.Count
            && fkCols.Select((c, i) => string.Equals(u.Columns[i], c, StringComparison.OrdinalIgnoreCase)).All(same => same));

    // A partial index holds only some rows, so it cannot find every child of a parent. An index
    // that is not ordered (hash) serves the lookup only when its key is exactly the FK columns.
    private static bool IsCoveredByIndex(
        IReadOnlyList<string> fkCols,
        IReadOnlyList<IndexDetail> indexes) =>
        indexes.Any(idx =>
            !idx.IsPartial
            && idx.StartsWith(fkCols)
            && (idx.IsOrdered()
                || (idx.Columns.Count == fkCols.Count && idx.Method.Contains("hash", StringComparison.OrdinalIgnoreCase))));
}
