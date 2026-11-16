using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Indexes;
using DbLens.Core.Models;
using FluentAssertions;
using static DbLens.Tests.TestSchema;

namespace DbLens.Tests;

/// <summary>
/// A dump restored yesterday on a server up for a month has one day of scan counts: the window
/// starts at whichever event emptied the counters last.
/// </summary>
public sealed class StatsWindowTests
{
    [Fact]
    public void TheMostRecentEventStartsTheWindow()
    {
        var window = StatsWindow.From(serverStartDays: 30, createdDays: 90, restoredDays: 1.5, statsResetDays: 10, restoreVisible: true);

        window.Days.Should().Be(1.5);
        window.Since.Should().Be(StatsWindowStart.DatabaseRestored);
        window.SinceText.Should().Be("since the database was restored");
    }

    [Fact]
    public void UnknownEventsLeaveTheServerStart()
    {
        var window = StatsWindow.From(serverStartDays: 30, createdDays: null, restoredDays: null, statsResetDays: null, restoreVisible: false);

        window.Days.Should().Be(30);
        window.Since.Should().Be(StatsWindowStart.ServerStart);
    }

    [Fact]
    public void ADatabaseOlderThanTheServerStillCountsFromTheStart()
    {
        StatsWindow.From(serverStartDays: 2, createdDays: 400, restoredDays: null, statsResetDays: null, restoreVisible: true)
            .Since.Should().Be(StatsWindowStart.ServerStart);
    }

    [Fact]
    public void ARecentRestore_SkipsTheRuleAndSaysWhy()
    {
        var result = Analyze(StatsWindow.From(30, null, 1, null, restoreVisible: true));

        result.Findings.Should().NotContain(f => f.RuleId == IndexRuleIds.UnusedIndex);
        result.Caveats.Should().Contain(c =>
            c.Contains("cover only 1 day(s), since the database was restored") && c.Contains("needs 7"));
    }

    [Fact]
    public void AnInvisibleRestore_LowersConfidenceAndSaysSo()
    {
        var result = Analyze(StatsWindow.From(30, null, null, null, restoreVisible: false));

        result.Findings.Should().ContainSingle(f => f.RuleId == IndexRuleIds.UnusedIndex)
            .Which.Confidence.Should().BeLessThan(0.9);
        result.Caveats.Should().Contain(c => c.Contains("cannot see when the database was created or restored"));
    }

    [Fact]
    public void AVisibleWindow_StatesItsDaysAndStart()
    {
        var finding = Analyze(StatsWindow.From(30, 45, null, null, restoreVisible: true))
            .Findings.Single(f => f.RuleId == IndexRuleIds.UnusedIndex);

        finding.Confidence.Should().Be(0.9);
        finding.Title.Should().Contain("30 day(s) of statistics");
        finding.Metadata!["stats_since"].Should().Be(nameof(StatsWindowStart.ServerStart));
        finding.Metadata["stats_window_days"].Should().Be(30.0);
    }

    private static AnalyzeIndexesResult Analyze(StatsWindow window)
    {
        var table = Table("orders", [Col("id", "int"), Col("status")]) with
        {
            Indexes = [new IndexDetail("ix_status", ["status"], IsUnique: false, IsPrimary: false, "btree", "", IsPartial: false, null, null, null)],
        };
        var stats = new IndexUsageStats(new Dictionary<(string, string, string), long> { [("public", "orders", "ix_status")] = 0 }, window);

        return IndexAnalysisEngine.Analyze([table], stats, new AnalyzeIndexesRequest(), DatabaseDialect.PostgreSql);
    }
}
