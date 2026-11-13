using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Fragmentation;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for PostgresInspector.AnalyzeFragmentationAsync.
/// Requires the dblens-postgres container to be running (docker-compose up postgres) with the
/// pgstattuple extension created by db/postgres/01-schema.sql.
/// Skipped automatically when PostgreSQL is not reachable.
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
public sealed class PostgresFragmentationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresFragmentationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private bool ShouldSkip => !_fixture.IsReachable;


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_PgStatTupleInstalled_StatsAreAvailable()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "public"));

        result.StatsAvailable.Should().BeTrue(
            "db/postgres/01-schema.sql creates the pgstattuple extension");
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_DemoDatabaseIndexesAreTooSmallToReport()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "public"));

        // The demo database holds only seed data, so every index sits well below the
        // default 1000-page floor and is deliberately skipped as not actionable.
        result.Findings.Should().BeEmpty();
        result.Summary.IndexesSkippedTooSmall.Should().BeGreaterThan(0);
        result.Caveats.Should().Contain(c => c.Contains("skipped"));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_MinPageCountZero_ReadsIndexesFromCatalog()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "public", MinPageCount: 0));

        result.StatsAvailable.Should().BeTrue();
        result.Summary.IndexesAnalyzed.Should().BeGreaterThan(0,
            "the public schema contains btree indexes");
        result.Summary.IndexesSkippedTooSmall.Should().Be(0);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_SummaryContainsAllRuleKeys()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "public"));

        foreach (var rule in FragmentationRuleIds.All)
            result.Summary.ByRule.Should().ContainKey(rule);

        foreach (var severity in Enum.GetValues<Severity>())
            result.Summary.BySeverity.Should().ContainKey(severity.ToString());
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_SourceDescriptionMentionsBtreeLimitation()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "public", MinPageCount: 0));

        result.Caveats.Should().Contain(c => c.Contains("btree"));
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
    public async Task AnalyzeFragmentation_IncludePageDensity_PopulatesDensityWithoutError()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(
                Schema: "public", MinPageCount: 0, IncludePageDensity: true));

        result.StatsAvailable.Should().BeTrue();
        result.Caveats.Should().NotContain(c => c.Contains("Page density was not collected"));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeFragmentation_DisclaimerIsAlwaysPresent()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeFragmentationAsync(
            new AnalyzeFragmentationRequest(Schema: "public"));

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
    }
}
