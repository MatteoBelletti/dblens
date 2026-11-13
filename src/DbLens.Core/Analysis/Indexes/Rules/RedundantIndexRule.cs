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

            // Candidates: non-unique, non-primary, non-partial indexes.
            // These are the ones that might be superseded by a longer index.
            var candidates = table.Indexes
                .Where(i => !i.IsUnique && !i.IsPrimary && !i.IsPartial)
                .ToList();

            // All non-partial indexes are potential supersets (unique or not, PK or not).
            var potential = table.Indexes
                .Where(i => !i.IsPartial)
                .ToList();

            foreach (var a in candidates)
            {
                var superset = potential.FirstOrDefault(b =>
                    !string.Equals(b.Name, a.Name, StringComparison.Ordinal) &&
                    b.Columns.Count > a.Columns.Count &&
                    IsPrefix(a.Columns, b.Columns));

                if (superset is null) continue;

                yield return new Finding(
                    RuleId:      IndexRuleIds.RedundantIndex,
                    Severity:    Severity.Low,
                    Category:    FindingCategory.Structure,
                    Confidence:  1.0,
                    Evidence:  Evidence.Derived,
                    Table:       tableQualified,
                    Columns:     a.Columns,
                    Title:       $"Index '{a.Name}' is redundant (prefix of '{superset.Name}')",
                    Description: $"Index '{a.Name}' on {tableQualified} " +
                                 $"({string.Join(", ", a.Columns)}) is an exact prefix of " +
                                 $"'{superset.Name}' ({string.Join(", ", superset.Columns)}). " +
                                 "Any query covered by the shorter index is already covered by the longer one.",
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

    private static bool IsPrefix(IReadOnlyList<string> prefix, IReadOnlyList<string> full)
    {
        if (prefix.Count >= full.Count) return false;
        for (var i = 0; i < prefix.Count; i++)
        {
            if (!string.Equals(prefix[i], full[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
