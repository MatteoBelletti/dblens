using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Indexes.Rules;

public static class UnusedIndexRule
{
    public static IEnumerable<Finding> Apply(
        IReadOnlyList<TableDetail> tables,
        IndexUsageStats stats,
        DatabaseDialect dialect)
    {
        foreach (var table in tables)
        {
            var tableQualified = table.Qualified();

            foreach (var idx in table.Indexes)
            {
                // PK and unique indexes serve constraint enforcement — skip.
                // Partial indexes have limited scope and are intentionally narrow — skip.
                if (idx.IsPrimary || idx.IsUnique || idx.IsPartial) continue;

                var key = (table.Schema, table.Name, idx.Name);
                if (!stats.ScanCounts.TryGetValue(key, out var scans)) continue;
                if (scans > 0) continue;

                var sqlFix = IndexSql.DropIndex(table.Schema, table.Name, idx.Name, dialect);

                var days = Num.F0(stats.Window.Days);

                yield return new Finding(
                    RuleId:      IndexRuleIds.UnusedIndex,
                    Severity:    Severity.Low,
                    Category:    FindingCategory.Performance,
                    // A restore the engine could not report may have emptied the counters later.
                    Confidence:  stats.Window.RestoreVisible ? 0.9 : 0.6,
                    Evidence:  Evidence.Measured,
                    Table:       tableQualified,
                    Columns:     idx.Columns,
                    Title:       $"Index '{idx.Name}' has not been used in {days} day(s) of statistics",
                    Description: $"Index '{idx.Name}' on {tableQualified} " +
                                 $"({string.Join(", ", idx.Columns)}) recorded 0 scans " +
                                 $"{stats.Window.SinceText}, {days} day(s) ago. " +
                                 "Verify manually before dropping — a workload that runs monthly or yearly may not have run yet.",
                    SqlFix:      SqlProposal.Ddl(sqlFix, "Drops the unused index."),
                    Metadata:    new Dictionary<string, object>
                    {
                        ["idx_scan"]          = 0L,
                        ["stats_window_days"] = Math.Round(stats.Window.Days, 2),
                        ["stats_since"]       = stats.Window.Since.ToString(),
                    });
            }
        }
    }
}
