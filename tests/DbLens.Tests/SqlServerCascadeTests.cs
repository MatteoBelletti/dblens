using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Cascade;
using DbLens.SqlServer;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for SqlServerInspector.CheckCascadePathsAsync.
/// Requires the dblens-sqlserver container to be running (docker-compose up sqlserver).
/// Skipped automatically when SQL Server is not reachable.
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
public sealed class SqlServerCascadeTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture = fixture;

    private bool ShouldSkip => !_fixture.IsReachable;


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_UsersTable_ReturnsPathWithDepthAtLeast3()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("dbo.users"));

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
            new CheckCascadePathsRequest("dbo.users"));

        result.Findings.Should().Contain(f =>
            f.RuleId == CascadeRuleIds.BreaksWithSetNull &&
            f.Table.EndsWith(".audit_logs"));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SelfReference_IsNoActionOnSqlServer()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // SQL Server forbids a self-referencing ON DELETE CASCADE (Msg 1785), so the demo hierarchy
        // is NO ACTION here. It must be reported as a blocking constraint, never as a cascade.
        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("dbo.categories"));

        result.Findings.Should().NotContain(f => f.RuleId == CascadeRuleIds.SelfReferencing);
        result.Findings.Should().Contain(f =>
            f.RuleId == CascadeRuleIds.BreaksWithRestrict && f.Table.EndsWith(".categories"));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_DisclaimerAlwaysPresent()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("dbo.users"));

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SummaryFindingAlwaysPresent()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("dbo.users"));

        result.Findings.Should().Contain(f => f.RuleId == CascadeRuleIds.Summary);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SummaryByRule_ContainsAllCascadeRules()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("dbo.users"));

        foreach (var ruleId in CascadeRuleIds.All)
            result.Summary.ByRule.Should().ContainKey(ruleId);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SummaryBySeverity_ContainsAllSeverities()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("dbo.users"));

        foreach (var sev in Enum.GetNames<Severity>())
            result.Summary.BySeverity.Should().ContainKey(sev);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_Metadata_AreCorrect()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("dbo.users"));

        result.Dialect.Should().Be("SqlServer");
        result.Database.Should().Be("dblens_demo");
        result.GeneratedAt.Should().NotBeNullOrWhiteSpace();
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_SchemaFilter_RestrictsResults()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest(Schema: "dbo"));

        result.Paths.Should().NotBeEmpty();
        result.Paths.Should().OnlyContain(p => p.RootTable.StartsWith("dbo."));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_UsersTable_PathContainsOrderItems()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("dbo.users"));

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

        result.Findings.Should().OnlyContain(f => f.RuleId == CascadeRuleIds.Summary);
    }
}
