using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Fragmentation;
using DbLens.Core.Analysis.Indexes;
using DbLens.Core.Models;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Unit tests for FragmentationAnalysisEngine — pure in-memory logic with no database access.
/// </summary>
public sealed class FragmentationAnalysisTests
{
    // ── Builders ───────────────────────────────────────────────────────────────

    private static IndexFragmentation Index(
        string name,
        double fragmentation,
        long pageCount = 5000,
        double? density = null,
        bool isPrimary = false,
        string schema = "public",
        string table = "orders",
        string method = "btree") =>
        new(schema, table, name, fragmentation, pageCount, density, isPrimary, method);

    private static IndexFragmentationStats Stats(params IndexFragmentation[] indexes) =>
        new(indexes, StatsAvailable: true, UnavailableReason: null,
            SourceDescription: "test source");

    private static AnalyzeFragmentationResult Analyze(
        IndexFragmentationStats stats,
        AnalyzeFragmentationRequest? request = null,
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        StatsWindow? window = null) =>
        FragmentationAnalysisEngine.Analyze(
            stats,
            request ?? new AnalyzeFragmentationRequest(),
            dialect,
            window);

    private static IEnumerable<Finding> OfRule(AnalyzeFragmentationResult r, string ruleId) =>
        r.Findings.Where(f => f.RuleId == ruleId);

    // ── Summary contract ───────────────────────────────────────────────────────

    [Fact]
    public void Summary_AlwaysContainsEveryRuleAndSeverityKey()
    {
        var result = Analyze(Stats(Index("idx_a", 0)));

        foreach (var rule in FragmentationRuleIds.All)
            result.Summary.ByRule.Should().ContainKey(rule);

        foreach (var severity in Enum.GetValues<Severity>())
            result.Summary.BySeverity.Should().ContainKey(severity.ToString());
    }

    [Fact]
    public void Empty_HasNoFindingsAndReportsStatsAvailable()
    {
        var result = FragmentationAnalysisEngine.Empty();

        result.Findings.Should().BeEmpty();
        result.StatsAvailable.Should().BeTrue();
        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
    }

    // ── Unavailable statistics ─────────────────────────────────────────────────

    [Fact]
    public void UnavailableStats_ProducesNoFindingsAndSurfacesReason()
    {
        var stats = new IndexFragmentationStats(
            Indexes: [],
            StatsAvailable: false,
            UnavailableReason: "pgstattuple is not installed",
            SourceDescription: "unavailable");

        var result = Analyze(stats);

        result.StatsAvailable.Should().BeFalse();
        result.Findings.Should().BeEmpty();
        result.Caveats.Should().Contain(c => c.Contains("pgstattuple"));
    }

    [Fact]
    public void UnavailableStats_IsDistinguishableFromHealthyIndexes()
    {
        var unavailable = Analyze(new IndexFragmentationStats(
            [], StatsAvailable: false, UnavailableReason: "no permission", SourceDescription: "x"));
        var healthy = Analyze(Stats(Index("idx_a", 1.0)));

        unavailable.StatsAvailable.Should().BeFalse();
        healthy.StatsAvailable.Should().BeTrue();
        unavailable.Findings.Should().BeEmpty();
        healthy.Findings.Should().BeEmpty();
    }

    // ── Thresholds ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(45.0, FragmentationRuleIds.HighFragmentation)]
    [InlineData(30.0, FragmentationRuleIds.HighFragmentation)]
    [InlineData(29.9, FragmentationRuleIds.ModerateFragmentation)]
    [InlineData(10.0, FragmentationRuleIds.ModerateFragmentation)]
    public void Fragmentation_MapsToExpectedRule(double percent, string expectedRule)
    {
        var result = Analyze(Stats(Index("idx_a", percent)));

        result.Findings.Should().ContainSingle().Which.RuleId.Should().Be(expectedRule);
    }

    [Fact]
    public void Fragmentation_BelowModerateThresholdProducesNoFinding()
    {
        var result = Analyze(Stats(Index("idx_a", 9.9)));

        result.Findings.Should().BeEmpty();
    }

    [Fact]
    public void Fragmentation_HighIsMoreSevereThanModerate()
    {
        var high = Analyze(Stats(Index("idx_a", 50))).Findings.Single();
        var moderate = Analyze(Stats(Index("idx_a", 15))).Findings.Single();

        high.Severity.Should().Be(Severity.Medium);
        moderate.Severity.Should().Be(Severity.Low);
    }

    [Fact]
    public void Fragmentation_CustomThresholdsAreHonoured()
    {
        var request = new AnalyzeFragmentationRequest(
            HighFragmentationThreshold: 80,
            ModerateFragmentationThreshold: 70);

        var result = Analyze(Stats(Index("idx_a", 50)), request);

        result.Findings.Should().BeEmpty();
    }

    // ── Small index filtering ──────────────────────────────────────────────────

    [Fact]
    public void SmallIndexes_AreSkippedAndCounted()
    {
        var result = Analyze(Stats(
            Index("idx_small", 90, pageCount: 10),
            Index("idx_big", 90, pageCount: 5000)));

        result.Findings.Should().ContainSingle();
        result.Summary.IndexesAnalyzed.Should().Be(1);
        result.Summary.IndexesSkippedTooSmall.Should().Be(1);
        result.Caveats.Should().Contain(c => c.Contains("skipped"));
    }

    [Fact]
    public void SmallIndexes_ExcludedFromMaxFragmentation()
    {
        var result = Analyze(Stats(
            Index("idx_small", 99, pageCount: 5),
            Index("idx_big", 40, pageCount: 5000)));

        result.Summary.MaxFragmentationPercent.Should().Be(40);
    }

    // ── Page density ───────────────────────────────────────────────────────────

    [Fact]
    public void LowPageDensity_FiresWhenDensityCollected()
    {
        var request = new AnalyzeFragmentationRequest(IncludePageDensity: true);
        var result = Analyze(Stats(Index("idx_a", 0, density: 40)), request);

        OfRule(result, FragmentationRuleIds.LowPageDensity).Should().ContainSingle();
    }

    [Fact]
    public void LowPageDensity_DoesNotFireWhenDensityAboveThreshold()
    {
        var request = new AnalyzeFragmentationRequest(IncludePageDensity: true);
        var result = Analyze(Stats(Index("idx_a", 0, density: 95)), request);

        OfRule(result, FragmentationRuleIds.LowPageDensity).Should().BeEmpty();
    }

    [Fact]
    public void PageDensity_NotCollectedAddsExplanatoryCaveat()
    {
        var result = Analyze(Stats(Index("idx_a", 0)));

        result.Caveats.Should().Contain(c => c.Contains("Page density was not collected"));
        OfRule(result, FragmentationRuleIds.LowPageDensity).Should().BeEmpty();
    }

    // ── SQL fixes are dialect-correct ──────────────────────────────────────────

    [Fact]
    public void SqlFix_PostgresUsesConcurrentReindex()
    {
        var result = Analyze(Stats(Index("idx_a", 50)), dialect: DatabaseDialect.PostgreSql);

        result.Findings.Single().SqlFix!.Sql.Should().Contain("REINDEX INDEX CONCURRENTLY");
    }

    [Fact]
    public void SqlFix_SqlServerRebuildsWhenHighlyFragmented()
    {
        var result = Analyze(Stats(Index("idx_a", 50)), dialect: DatabaseDialect.SqlServer);

        result.Findings.Single().SqlFix!.Sql.Should().Contain("REBUILD");
    }

    [Fact]
    public void SqlFix_SqlServerReorganizesWhenModeratelyFragmented()
    {
        var result = Analyze(Stats(Index("idx_a", 15)), dialect: DatabaseDialect.SqlServer);

        result.Findings.Single().SqlFix!.Sql.Should().Contain("REORGANIZE");
    }

    // ── Metadata ───────────────────────────────────────────────────────────────

    [Fact]
    public void Finding_CarriesIndexNameAndPageCountInMetadata()
    {
        var result = Analyze(Stats(Index("idx_orders_status", 55, pageCount: 2048)));

        var finding = result.Findings.Single();
        finding.Metadata.Should().ContainKey("indexName");
        finding.Metadata!["indexName"].Should().Be("idx_orders_status");
        finding.Metadata["pageCount"].Should().Be(2048L);
        finding.Category.Should().Be(FindingCategory.Performance);
    }

    // ── Truncation ─────────────────────────────────────────────────────────────

    [Fact]
    public void Truncation_CapsFindingsAndAddsCaveat()
    {
        var indexes = Enumerable.Range(0, DiagnosticLimits.MaxFindingCount + 25)
            .Select(i => Index($"idx_{i}", 50))
            .ToArray();

        var result = Analyze(Stats(indexes));

        result.Truncated.Should().BeTrue();
        result.Findings.Should().HaveCount(DiagnosticLimits.MaxFindingCount);
        result.Caveats.Should().Contain(c => c.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IndexesThatWereNotMeasured_AreCountedAndNamedInACaveat()
    {
        var stats = Stats(Index("idx_a", 5)) with
        {
            NotMeasured = [new NotMeasuredIndex("public", "orders", "idx_big", "canceling statement due to statement timeout"), new NotMeasuredIndex("public", "lines", "idx_late", "over the time budget")],
        };

        var result = Analyze(stats);

        result.Summary.IndexesNotMeasured.Should().Be(2);
        result.Caveats.Should().Contain(c => c.Contains("2 index(es) were not measured") && c.Contains("'idx_big' on 'public.orders' (canceling statement due to statement timeout)") && c.Contains("'idx_late' on 'public.lines' (over the time budget)"));
    }

    [Fact]
    public void ManyIndexesNotMeasured_AreNamedUpToALimit_AndTheRestCounted()
    {
        var stats = Stats() with { NotMeasured = [.. Enumerable.Range(0, 13).Select(i => new NotMeasuredIndex("public", "t", $"idx_{i:00}", "over the time budget"))] };

        var caveat = Analyze(stats).Caveats.Single(c => c.Contains("not measured"));

        caveat.Should().Contain("13 index(es)").And.Contain("'idx_09'").And.NotContain("'idx_10'").And.Contain("and 3 more");
    }

    [Theory]
    [InlineData(StatsWindowStart.DatabaseRestored, 2, true)]
    [InlineData(StatsWindowStart.DatabaseCreated, 2, true)]
    [InlineData(StatsWindowStart.DatabaseRestored, 90, false)]
    [InlineData(StatsWindowStart.ServerStart, 2, false)]
    public void ARecentRestore_SaysTheFragmentationReflectsIt(StatsWindowStart since, double days, bool expected)
    {
        var result = Analyze(Stats(Index("idx_a", 5)), window: new StatsWindow(days, since, RestoreVisible: true));

        (result.Caveats.Any(c => c.Contains("reflects the restore, not production"))).Should().Be(expected);
    }
}
