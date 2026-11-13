using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Fragmentation;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for SqlServerInspector.AnalyzeFragmentationAsync.
/// Requires the dblens-sqlserver container to be running (docker-compose up sqlserver).
/// The sa login used by the demo has VIEW DATABASE STATE implicitly.
/// Skipped automatically when SQL Server is not reachable.
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
public sealed class SqlServerFragmentationTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SqlServerFragmentationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private bool ShouldSkip => !_fixture.IsReachable;


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_SaLogin_StatsAreAvailable()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "dbo"));

        result.StatsAvailable.Should().BeTrue(
            "the demo sa login can read sys.dm_db_index_physical_stats");
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_DemoDatabaseIndexesAreTooSmallToReport()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "dbo"));

        // The demo database holds only seed data, so every index sits well below the
        // default 1000-page floor and is deliberately skipped as not actionable.
        result.Findings.Should().BeEmpty();
        result.Summary.IndexesSkippedTooSmall.Should().BeGreaterThan(0);
        result.Caveats.Should().Contain(c => c.Contains("skipped"));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_MinPageCountZero_ReadsIndexesFromDmv()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "dbo", MinPageCount: 0));

        result.StatsAvailable.Should().BeTrue();
        result.Summary.IndexesAnalyzed.Should().BeGreaterThan(0,
            "the dbo schema contains clustered and nonclustered indexes");
        result.Summary.IndexesSkippedTooSmall.Should().Be(0);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_SummaryContainsAllRuleKeys()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "dbo"));

        foreach (var rule in FragmentationRuleIds.All)
            result.Summary.ByRule.Should().ContainKey(rule);

        foreach (var severity in Enum.GetValues<Severity>())
            result.Summary.BySeverity.Should().ContainKey(severity.ToString());
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_LimitedModeIsReportedInCaveats()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "dbo", MinPageCount: 0));

        result.Caveats.Should().Contain(c => c.Contains("LIMITED"));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_IncludePageDensity_SwitchesToSampledMode()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(
                Schema: "dbo", MinPageCount: 0, IncludePageDensity: true));

        result.StatsAvailable.Should().BeTrue();
        result.Caveats.Should().Contain(c => c.Contains("SAMPLED"));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_UnknownSchema_ReturnsEmptyResult()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "no_such_schema"));

        result.Findings.Should().BeEmpty();
        result.Summary.TotalFindings.Should().Be(0);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_DisclaimerIsAlwaysPresent()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "dbo"));

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
    }
}
