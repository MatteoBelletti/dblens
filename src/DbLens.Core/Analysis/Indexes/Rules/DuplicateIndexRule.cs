using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Indexes.Rules;

public static class DuplicateIndexRule
{
    public static IEnumerable<Finding> Apply(
        IReadOnlyList<TableDetail> tables,
        DatabaseDialect dialect)
    {
        foreach (var table in tables)
        {
            var tableQualified = table.Qualified();

            // Duplicates are the same index twice: same key and key order, uniqueness, access method
            // and INCLUDE columns. A hash index beside a b-tree, or an index that carries extra
            // columns or sorts (a, b DESC) instead of (a, b), serves queries the other cannot. Partial indexes have dialect-specific predicates and
            // cannot be safely deduplicated.
            var groups = table.Indexes
                .Where(i => !i.IsPartial)
                .GroupBy(i => (
                    Cols: string.Join(",", i.Columns.Select(c => c.ToLowerInvariant())),
                    Order: i.KeyOrder(i.Columns.Count),
                    Included: string.Join(",", i.Included().Select(c => c.ToLowerInvariant()).Order(StringComparer.Ordinal)),
                    Method: i.Method.ToLowerInvariant(),
                    i.IsUnique))
                .Where(g => g.Count() > 1);

            foreach (var grp in groups)
            {
                // Keep the PK (if any), otherwise keep the first alphabetically.
                var ordered = grp
                    .OrderByDescending(i => i.IsPrimary)
                    .ThenBy(i => i.Name, StringComparer.Ordinal)
                    .ToList();
                var canonical = ordered[0];

                for (var j = 1; j < ordered.Count; j++)
                {
                    var dup = ordered[j];
                    yield return new Finding(
                        RuleId:      IndexRuleIds.DuplicateIndex,
                        Severity:    Severity.Medium,
                        Category:    FindingCategory.Structure,
                        Confidence:  1.0,
                        Evidence:  Evidence.Derived,
                        Table:       tableQualified,
                        Columns:     dup.Columns,
                        Title:       $"Index '{dup.Name}' is a duplicate of '{canonical.Name}'",
                        Description: $"Index '{dup.Name}' on {tableQualified} " +
                                     $"({string.Join(", ", dup.Columns)}) has the same columns, access method " +
                                     $"and uniqueness as '{canonical.Name}'. One of them is redundant.",
                        SqlFix:      SqlProposal.Ddl(
                            IndexSql.DropIndex(table.Schema, table.Name, dup.Name, dialect),
                            "Drops the duplicate index."),
                        Metadata:    new Dictionary<string, object>
                        {
                            ["canonical_index"] = canonical.Name,
                            ["duplicate_index"] = dup.Name
                        });
                }
            }
        }
    }

}
