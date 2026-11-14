using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Indexes.Rules;

public static class RedundantIndexRule
{
    public static IEnumerable<Finding> Apply(
        IReadOnlyList<TableDetail> tables,
        DatabaseDialect dialect)
    {
        foreach (var table in tables)
        {
            var tableQualified = table.Qualified();

            // Candidates: non-unique, non-primary, non-partial ordered indexes.
            // These are the ones that might be superseded by another index.
            var candidates = table.Indexes
                .Where(i => !i.IsUnique && !i.IsPrimary && !i.IsPartial && i.IsOrdered())
                .ToList();

            // All non-partial ordered indexes are potential supersets (unique or not, PK or not).
            var potential = table.Indexes
                .Where(i => !i.IsPartial && i.IsOrdered())
                .ToList();

            foreach (var a in candidates)
            {
                var superset = potential.FirstOrDefault(b =>
                    !string.Equals(b.Name, a.Name, StringComparison.Ordinal) && Supersedes(b, a));

                if (superset is null) continue;

                var sameKey = superset.Columns.Count == a.Columns.Count;

                yield return new Finding(
                    RuleId:      IndexRuleIds.RedundantIndex,
                    Severity:    Severity.Low,
                    Category:    FindingCategory.Structure,
                    Confidence:  1.0,
                    Evidence:  Evidence.Derived,
                    Table:       tableQualified,
                    Columns:     a.Columns,
                    Title:       sameKey
                        ? $"Index '{a.Name}' is redundant (covered by '{superset.Name}')"
                        : $"Index '{a.Name}' is redundant (prefix of '{superset.Name}')",
                    Description: $"Index '{a.Name}' on {tableQualified} ({string.Join(", ", a.Columns)}) " +
                                 (sameKey
                                     ? $"has the same key as '{superset.Name}', which " +
                                       (superset.IsUnique ? "also enforces uniqueness" : "carries more columns") + ". "
                                     : $"is an exact prefix of '{superset.Name}' ({string.Join(", ", superset.Columns)}). ") +
                                 "Any query covered by this index is already covered by the other one.",
                    SqlFix:      SqlProposal.Ddl(
                        IndexSql.DropIndex(table.Schema, table.Name, a.Name, dialect),
                        "Drops the redundant index."),
                    Metadata:    new Dictionary<string, object>
                    {
                        ["superset_index"] = superset.Name
                    });
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="b"/> can do everything <paramref name="a"/> does: its key starts with
    /// a's key, in the same order, and it holds every column a carries. With the same key it must offer more — uniqueness
    /// or extra columns — or the two are plain duplicates, which is the other rule's finding.
    /// </summary>
    private static bool Supersedes(IndexDetail b, IndexDetail a)
    {
        if (!b.StartsWith(a.Columns) || b.KeyOrder(a.Columns.Count) != a.KeyOrder(a.Columns.Count)) return false;

        var carried = new HashSet<string>(b.Columns.Concat(b.Included()), StringComparer.OrdinalIgnoreCase);
        if (!a.Included().All(carried.Contains)) return false;

        return b.Columns.Count > a.Columns.Count
               || b.IsUnique
               || b.Included().Count > a.Included().Count;
    }
}
