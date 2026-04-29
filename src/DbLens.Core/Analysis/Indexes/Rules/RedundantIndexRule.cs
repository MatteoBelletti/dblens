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
            var tableQualified = $"{table.Schema}.{table.Name}";

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
                    RuleId: "redundant-index",
                    Severity: Severity.Low,
                    Category: FindingCategory.Structure,
                    Confidence: 1.0,
                    Table: tableQualified,
                    Columns: a.Columns,
                    Title: $"Indice '{a.Name}' ridondante (prefisso di '{superset.Name}')",
                    Description:
                        $"L'indice '{a.Name}' su {tableQualified} " +
                        $"({string.Join(", ", a.Columns)}) è un prefisso esatto di " +
                        $"'{superset.Name}' ({string.Join(", ", superset.Columns)}). " +
                        "Qualsiasi query coperta dal primo è già coperta dal secondo.",
                    SqlFix: DuplicateIndexRule.BuildDropIndex(table.Schema, table.Name, a.Name, dialect),
                    Metadata: new Dictionary<string, object>
                    {
                        ["superset_index"] = superset.Name
                    }
                );
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
