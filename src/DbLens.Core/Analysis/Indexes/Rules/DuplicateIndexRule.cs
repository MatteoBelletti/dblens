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
            var tableQualified = $"{table.Schema}.{table.Name}";

            // Group non-partial indexes by (column sequence lowercased, isUnique).
            // Partial indexes have dialect-specific predicates and cannot be safely deduplicated.
            var groups = table.Indexes
                .Where(i => !i.IsPartial)
                .GroupBy(i => (
                    Cols: string.Join(",", i.Columns.Select(c => c.ToLowerInvariant())),
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
                        RuleId: "duplicate-index",
                        Severity: Severity.Medium,
                        Category: FindingCategory.Structure,
                        Confidence: 1.0,
                        Table: tableQualified,
                        Columns: dup.Columns,
                        Title: $"Indice '{dup.Name}' duplicato di '{canonical.Name}'",
                        Description:
                            $"L'indice '{dup.Name}' su {tableQualified} " +
                            $"({string.Join(", ", dup.Columns)}) ha le stesse colonne e unicità " +
                            $"di '{canonical.Name}'. Uno dei due è ridondante.",
                        SqlFix: BuildDropIndex(table.Schema, table.Name, dup.Name, dialect),
                        Metadata: new Dictionary<string, object>
                        {
                            ["canonical_index"] = canonical.Name,
                            ["duplicate_index"] = dup.Name
                        }
                    );
                }
            }
        }
    }

    public static string BuildDropIndex(
        string schema, string table, string indexName, DatabaseDialect dialect)
        => dialect == DatabaseDialect.PostgreSql
            ? $"DROP INDEX CONCURRENTLY {schema}.{indexName};"
            : $"DROP INDEX [{indexName}] ON [{schema}].[{table}];";
}
