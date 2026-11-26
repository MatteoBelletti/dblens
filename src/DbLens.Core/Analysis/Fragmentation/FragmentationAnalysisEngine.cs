using DbLens.Core.Analysis.Indexes;
using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Fragmentation;

/// <summary>
/// Pure in-memory fragmentation analysis. No database access — operates on normalized physical
/// statistics supplied by the caller (dialect-specific inspector).
/// </summary>
public static class FragmentationAnalysisEngine
{
    public const string Disclaimer =
        "Fragmentation is a point-in-time physical measurement that changes with write activity. " +
        "Rebuilding an index is I/O intensive and takes locks — on PostgreSQL prefer REINDEX CONCURRENTLY, " +
        "on SQL Server prefer ONLINE = ON where the edition supports it. " +
        "Fragmentation matters mainly for range scans; it has little effect on singleton seeks, " +
        "and on SSD-backed storage its impact is smaller than on spinning disks. " +
        "SCOPE: this reads the current fragmentation and page-density numbers and nothing else. " +
        "It does NOT know the index's configured fill factor, its page-split history, how quickly it " +
        "re-fragments after a rebuild, or whether any query actually range-scans it — all of which " +
        "determine whether a rebuild is worth doing and whether it will hold. " +
        "Treat this as one input to that decision, not the decision. " +
        "For a maintenance strategy, dedicated tooling reasons about far more signal than this: " +
        "sp_BlitzIndex and Ola Hallengren's maintenance solution on SQL Server, PgHero and " +
        "pganalyze on PostgreSQL.";

    private const int MaxNamedNotMeasured = 10;

    public static AnalyzeFragmentationResult Analyze(
        IndexFragmentationStats stats,
        AnalyzeFragmentationRequest request,
        DatabaseDialect dialect,
        StatsWindow? window = null)
    {
        List<string> caveats = [];

        if (!stats.StatsAvailable)
        {
            caveats.Add(stats.UnavailableReason
                ?? "Physical index statistics are not available on this database.");

            return Build(FindingSplit.Empty, caveats, 0, 0, 0, 0, statsAvailable: false);
        }

        var eligible = stats.Indexes.Where(i => i.PageCount >= request.MinPageCount).ToList();
        var skipped = stats.Indexes.Count - eligible.Count;

        List<Finding> findings = [];

        foreach (var index in eligible.OrderByDescending(i => i.FragmentationPercent))
        {
            if (index.FragmentationPercent >= request.HighFragmentationThreshold)
                findings.Add(BuildFragmentationFinding(index, request, dialect, high: true));
            else if (index.FragmentationPercent >= request.ModerateFragmentationThreshold)
                findings.Add(BuildFragmentationFinding(index, request, dialect, high: false));

            if (index.PageDensityPercent is { } density && density < request.LowPageDensityThreshold)
                findings.Add(BuildLowDensityFinding(index, density, request, dialect));
        }

        caveats.Add(stats.SourceDescription);

        var notMeasured = stats.NotMeasured ?? [];
        if (notMeasured.Count > 0)
            caveats.Add(
                $"{notMeasured.Count} index(es) were not measured, so they are neither reported nor cleared: " +
                string.Join("; ", notMeasured.Take(MaxNamedNotMeasured).Select(n => $"'{n.IndexName}' on '{n.Schema}.{n.Table}' ({n.Reason})")) +
                (notMeasured.Count > MaxNamedNotMeasured ? $"; and {notMeasured.Count - MaxNamedNotMeasured} more" : "") +
                ". Narrow the scope to the tables that matter and run it again.");

        // A PostgreSQL dump restore builds every index afresh. A SQL Server RESTORE copies the pages as they were, fragmentation included.
        if (dialect == DatabaseDialect.PostgreSql && window is { Since: StatsWindowStart.DatabaseCreated or StatsWindowStart.DatabaseRestored } && window.Days < request.RecentRestoreDays)
            caveats.Add(
                $"The database was {(window.Since == StatsWindowStart.DatabaseRestored ? "restored" : "created")} {Num.F0(window.Days)} day(s) ago, " +
                "and a restore from a dump builds its indexes afresh: the fragmentation measured here reflects the restore, not production. " +
                "Measure it on production or on a replica.");

        if (skipped > 0)
            caveats.Add(
                $"{skipped} index(es) were skipped because they are smaller than " +
                $"{request.MinPageCount} pages. Fragmentation on a small index is not actionable — " +
                "its pages typically sit within a single extent.");

        if (!request.IncludePageDensity)
            caveats.Add(
                "Page density was not collected (IncludePageDensity = false), so the " +
                "'idx-low-page-density' rule did not run. Enabling it uses a heavier scan mode.");

        var split = FindingSplit.Partition(findings);

        if (split.TruncationCaveat() is { } truncation)
            caveats.Add(truncation);

        var maxFragmentation = eligible.Count > 0 ? eligible.Max(i => i.FragmentationPercent) : 0;

        return Build(split, caveats, eligible.Count, skipped, notMeasured.Count, maxFragmentation, statsAvailable: true);
    }

    public static AnalyzeFragmentationResult Empty() =>
        Build(FindingSplit.Empty, [], 0, 0, 0, 0, statsAvailable: true);

    private static AnalyzeFragmentationResult Build(
        FindingSplit split,
        IReadOnlyList<string> caveats,
        int indexesAnalyzed,
        int indexesSkipped,
        int indexesNotMeasured,
        double maxFragmentation,
        bool statsAvailable) =>
        new(
            Findings: split.Findings,
            Signals: split.Signals,
            Summary: BuildSummary(split, indexesAnalyzed, indexesSkipped, indexesNotMeasured, maxFragmentation),
            Caveats: caveats,
            Truncated: split.Truncated,
            StatsAvailable: statsAvailable,
            Disclaimer: Disclaimer);

    private static Finding BuildFragmentationFinding(
        IndexFragmentation index,
        AnalyzeFragmentationRequest request,
        DatabaseDialect dialect,
        bool high)
    {
        var qualified = $"{index.Schema}.{index.Table}";
        var threshold = high ? request.HighFragmentationThreshold : request.ModerateFragmentationThreshold;
        var sizeMb = index.PageCount * 8.0 / 1024.0;

        return new Finding(
            RuleId:     high ? FragmentationRuleIds.HighFragmentation : FragmentationRuleIds.ModerateFragmentation,
            Severity:   high ? Severity.Medium : Severity.Low,
            Category:   FindingCategory.Performance,
            Confidence: 1.0,
            Evidence: Evidence.Measured,
            Table:      qualified,
            Columns:    null,
            Title:
                $"Index '{index.IndexName}' on '{qualified}' is {Num.F1(index.FragmentationPercent)}% fragmented",
            Description:
                $"'{index.IndexName}' has {Num.F1(index.FragmentationPercent)}% logical fragmentation across " +
                $"{Num.N0(index.PageCount)} pages ({Num.F1(sizeMb)} MB), at or above the {Num.F0(threshold)}% threshold. " +
                (high
                    ? "At this level a full rebuild is normally the right remedy — it rewrites the index and " +
                      "resets the fill factor."
                    : "At this level a reorganize is normally enough — it defragments the leaf level in place " +
                      "and is fully online and interruptible.") +
                " Fragmentation slows range scans by forcing extra I/O; it does not meaningfully affect " +
                "singleton seeks.",
            SqlFix:     SqlProposal.Ddl(BuildFix(index, dialect, high), "Rebuilds or reorganizes the index."),
            Metadata:   new Dictionary<string, object>
            {
                ["indexName"] = index.IndexName,
                ["fragmentationPercent"] = Math.Round(index.FragmentationPercent, 2),
                ["pageCount"] = index.PageCount,
                ["sizeMb"] = Math.Round(sizeMb, 2),
                ["isPrimary"] = index.IsPrimary,
                ["method"] = index.Method,
            });
    }

    private static Finding BuildLowDensityFinding(
        IndexFragmentation index,
        double density,
        AnalyzeFragmentationRequest request,
        DatabaseDialect dialect)
    {
        var qualified = $"{index.Schema}.{index.Table}";
        var wastedMb = index.PageCount * 8.0 / 1024.0 * (1 - density / 100.0);

        return new Finding(
            RuleId:     FragmentationRuleIds.LowPageDensity,
            Severity:   Severity.Low,
            Category:   FindingCategory.Performance,
            Confidence: 1.0,
            Evidence: Evidence.Measured,
            Table:      qualified,
            Columns:    null,
            Title:      $"Index '{index.IndexName}' on '{qualified}' is {Num.F1(density)}% full",
            Description:
                $"'{index.IndexName}' uses only {Num.F1(density)}% of the space in its pages, below the " +
                $"{Num.F0(request.LowPageDensityThreshold)}% threshold — roughly {Num.F1(wastedMb)} MB of the " +
                $"{Num.N0(index.PageCount)} allocated pages is empty. " +
                "Sparse pages mean more pages to read for the same rows and more memory consumed in the " +
                "buffer cache. This is normal for an index with a deliberately low fill factor, and expected " +
                "on a table with heavy deletes. " +
                "A rebuild repacks the pages.",
            SqlFix:     SqlProposal.Ddl(BuildFix(index, dialect, high: true), "Rebuilds the index to repack its pages."),
            Metadata:   new Dictionary<string, object>
            {
                ["indexName"] = index.IndexName,
                ["pageDensityPercent"] = Math.Round(density, 2),
                ["pageCount"] = index.PageCount,
                ["estimatedWastedMb"] = Math.Round(wastedMb, 2),
            });
    }

    private static string BuildFix(IndexFragmentation index, DatabaseDialect dialect, bool high) =>
        IndexSql.Defragment(index.Schema, index.Table, index.IndexName, rebuild: high, dialect);

    private static FragmentationSummary BuildSummary(
        FindingSplit split,
        int indexesAnalyzed,
        int indexesSkipped,
        int indexesNotMeasured,
        double maxFragmentation) =>
        new(
            TotalFindings: split.Findings.Count,
            TotalSignals: split.Signals.Count,
            IndexesAnalyzed: indexesAnalyzed,
            IndexesSkippedTooSmall: indexesSkipped,
            IndexesNotMeasured: indexesNotMeasured,
            MaxFragmentationPercent: Math.Round(maxFragmentation, 2),
            BySeverity: split.BySeverity(),
            ByRule: split.ByRule(FragmentationRuleIds.All));
}
