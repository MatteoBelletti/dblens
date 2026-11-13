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

                yield return new Finding(
                    RuleId:      IndexRuleIds.UnusedIndex,
                    Severity:    Severity.Low,
                    Category:    FindingCategory.Performance,
                    Confidence:  0.9,
                    Evidence:  Evidence.Measured,
                    Table:       tableQualified,
                    Columns:     idx.Columns,
                    Title:       $"Index '{idx.Name}' has not been used since the last DB restart",
                    Description: $"Index '{idx.Name}' on {tableQualified} " +
                                 $"({string.Join(", ", idx.Columns)}) recorded 0 scans " +
                                 $"since the last DB restart (uptime: {Num.F0(stats.UptimeDays)} day(s)). " +
                                 "Verify manually before dropping — stats reset on every restart.",
                    SqlFix:      SqlProposal.Ddl(sqlFix, "Drops the unused index."),
                    Metadata:    new Dictionary<string, object>
                    {
                        ["idx_scan"]     = 0L,
                        ["uptime_days"]  = stats.UptimeDays
                    });
            }
        }
    }
}
