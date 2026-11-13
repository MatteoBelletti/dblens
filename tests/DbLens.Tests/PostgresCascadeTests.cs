using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Cascade;
using DbLens.Postgres;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for PostgresInspector.CheckCascadePathsAsync.
/// Requires the dblens-postgres container to be running (docker-compose up postgres).
/// Skipped automatically when PostgreSQL is not reachable.
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
public sealed class PostgresCascadeTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresCascadeTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private bool ShouldSkip => !_fixture.IsReachable;


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_UsersTable_ReturnsPathWithDepthAtLeast3()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        // users → orders (CASCADE) → order_items (CASCADE) → item_reviews (CASCADE)
        result.Paths.Should().NotBeEmpty();
        result.Paths.Max(p => p.Depth).Should().BeGreaterThanOrEqualTo(3);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_NullStartingTable_ReturnsAllCascadeRoots()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest());

        // At minimum: users and categories are cascade-root tables
        result.Paths.Should().NotBeEmpty();
        result.Paths.Select(p => p.RootTable).Should()
            .Contain(t => t.EndsWith(".users") || t.EndsWith(".categories"));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SetNullChain_ProducesBreaksWithSetNullFinding()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // audit_logs.user_id → users(id) ON DELETE SET NULL
        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        result.Findings.Should().Contain(f =>
            f.RuleId == CascadeRuleIds.BreaksWithSetNull &&
            f.Table.EndsWith(".audit_logs"));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SelfReferencing_ProducesSelfReferencingFinding()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // categories.parent_id → categories(id) ON DELETE CASCADE
        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.categories"));

        result.Findings.Should().Contain(f =>
            f.RuleId == CascadeRuleIds.SelfReferencing &&
            f.Table.EndsWith(".categories"));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_DisclaimerAlwaysPresent()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SummaryFindingAlwaysPresent()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        result.Findings.Should().Contain(f => f.RuleId == CascadeRuleIds.Summary);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SummaryByRule_ContainsAllCascadeRules()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        foreach (var ruleId in CascadeRuleIds.All)
            result.Summary.ByRule.Should().ContainKey(ruleId);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SummaryBySeverity_ContainsAllSeverities()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        foreach (var sev in Enum.GetNames<Severity>())
            result.Summary.BySeverity.Should().ContainKey(sev);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_Metadata_AreCorrect()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        result.Dialect.Should().Be("PostgreSql");
        result.Database.Should().Be("dblens_demo");
        result.GeneratedAt.Should().NotBeNullOrWhiteSpace();
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SchemaFilter_RestrictsResults()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest(Schema: "public"));

        // All path root tables and step tables should be in the public schema
        result.Paths.Should().NotBeEmpty();
        result.Paths.Should().OnlyContain(p => p.RootTable.StartsWith("public."));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_UsersTable_PathContainsOrderItems()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        var allToTables = result.Paths
            .SelectMany(p => p.Steps)
            .Select(s => s.ToTable)
            .ToList();

        allToTables.Should().Contain(t => t.EndsWith(".order_items"));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_DemoGoodSchema_ProducesNoFindings_ExceptSummary()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest(Schema: "demo_good"));

        // demo_good schema has no cascade chains, so no diagnostic findings
        result.Findings.Should().OnlyContain(f => f.RuleId == CascadeRuleIds.Summary);
    }
}
