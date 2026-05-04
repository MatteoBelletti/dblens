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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_UsersTable_ReturnsPathWithDepthAtLeast3()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        // users → orders (CASCADE) → order_items (CASCADE) → item_reviews (CASCADE)
        result.Paths.Should().NotBeEmpty();
        result.Paths.Max(p => p.Depth).Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_NullStartingTable_ReturnsAllCascadeRoots()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest());

        // At minimum: users and categories are cascade-root tables
        result.Paths.Should().NotBeEmpty();
        result.Paths.Select(p => p.RootTable).Should()
            .Contain(t => t.EndsWith(".users") || t.EndsWith(".categories"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SetNullChain_ProducesBreaksWithSetNullFinding()
    {
        if (ShouldSkip) return;

        // audit_logs.user_id → users(id) ON DELETE SET NULL
        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        result.Findings.Should().Contain(f =>
            f.RuleId == CascadeRuleIds.BreaksWithSetNull &&
            f.Table.EndsWith(".audit_logs"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SelfReferencing_ProducesSelfReferencingFinding()
    {
        if (ShouldSkip) return;

        // categories.parent_id → categories(id) ON DELETE CASCADE
        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.categories"));

        result.Findings.Should().Contain(f =>
            f.RuleId == CascadeRuleIds.SelfReferencing &&
            f.Table.EndsWith(".categories"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_DisclaimerAlwaysPresent()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SummaryFindingAlwaysPresent()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        result.Findings.Should().Contain(f => f.RuleId == CascadeRuleIds.Summary);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SummaryByRule_ContainsAllCascadeRules()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        foreach (var ruleId in CascadeRuleIds.All)
            result.Summary.ByRule.Should().ContainKey(ruleId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SummaryBySeverity_ContainsAllSeverities()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        foreach (var sev in Enum.GetNames<Severity>())
            result.Summary.BySeverity.Should().ContainKey(sev);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_Metadata_AreCorrect()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        result.Dialect.Should().Be("PostgreSql");
        result.Database.Should().Be("dblens_demo");
        result.GeneratedAt.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SchemaFilter_RestrictsResults()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest(Schema: "public"));

        // All path root tables and step tables should be in the public schema
        result.Paths.Should().NotBeEmpty();
        result.Paths.Should().OnlyContain(p => p.RootTable.StartsWith("public."));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_UsersTable_PathContainsOrderItems()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));

        var allToTables = result.Paths
            .SelectMany(p => p.Steps)
            .Select(s => s.ToTable)
            .ToList();

        allToTables.Should().Contain(t => t.EndsWith(".order_items"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_DemoGoodSchema_ProducesNoFindings_ExceptSummary()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest(Schema: "demo_good"));

        // demo_good schema has no cascade chains, so no diagnostic findings
        result.Findings.Should().OnlyContain(f => f.RuleId == CascadeRuleIds.Summary);
    }
}
