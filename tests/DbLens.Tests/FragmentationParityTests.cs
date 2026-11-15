using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Fragmentation;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Cross-dialect parity tests for analyze_fragmentation.
/// Fragmentation percentages are physical measurements and legitimately differ between engines,
/// so parity is asserted on the response contract and rule vocabulary rather than on values.
/// Skipped automatically when either database is not reachable.
/// </summary>
public sealed class FragmentationParityTests(PostgresFixture pg, SqlServerFixture ss) :
    IClassFixture<PostgresFixture>,
    IClassFixture<SqlServerFixture>
{
    private readonly PostgresFixture _pg = pg;
    private readonly SqlServerFixture _ss = ss;

    private bool ShouldSkip => !_pg.IsReachable || !_ss.IsReachable;


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_StatsAvailableOnBothDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "public"));
        var ss = await _ss.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "dbo"));

        pg.StatsAvailable.Should().BeTrue();
        ss.StatsAvailable.Should().BeTrue();
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_SummaryKeys_MatchAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "public"));
        var ss = await _ss.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "dbo"));

        pg.Summary.ByRule.Keys.OrderBy(k => k)
            .Should().BeEquivalentTo(ss.Summary.ByRule.Keys.OrderBy(k => k));
        pg.Summary.BySeverity.Keys.OrderBy(k => k)
            .Should().BeEquivalentTo(ss.Summary.BySeverity.Keys.OrderBy(k => k));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_RuleIds_AreDrawnFromTheSameVocabulary()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "public", MinPageCount: 0));
        var ss = await _ss.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "dbo", MinPageCount: 0));

        pg.Findings.Select(f => f.RuleId).Should().BeSubsetOf(FragmentationRuleIds.All);
        ss.Findings.Select(f => f.RuleId).Should().BeSubsetOf(FragmentationRuleIds.All);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_BothDialectsReadAtLeastOneIndex()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "public", MinPageCount: 0));
        var ss = await _ss.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "dbo", MinPageCount: 0));

        pg.Summary.IndexesAnalyzed.Should().BeGreaterThan(0);
        ss.Summary.IndexesAnalyzed.Should().BeGreaterThan(0);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_SqlFixIsDialectAppropriate()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // Force findings by treating any measurable fragmentation as reportable.
        var request = new AnalyzeFragmentationRequest(
            MinPageCount: 0,
            HighFragmentationThreshold: 0,
            ModerateFragmentationThreshold: 0);

        var pg = await _pg.Inspector!.AnalyzeFragmentationAsync(request with { Schema = "public" });
        var ss = await _ss.Inspector!.AnalyzeFragmentationAsync(request with { Schema = "dbo" });

        pg.Findings.Where(f => f.SqlFix is not null)
            .Should().OnlyContain(f => f.SqlFix!.Sql.Contains("REINDEX"));
        ss.Findings.Where(f => f.SqlFix is not null)
            .Should().OnlyContain(f => f.SqlFix!.Sql.Contains("ALTER INDEX"));
    }
}
