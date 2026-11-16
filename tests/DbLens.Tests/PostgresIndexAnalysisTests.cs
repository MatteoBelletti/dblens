using DbLens.Core.Analysis.Indexes;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for PostgresInspector.AnalyzeIndexesAsync.
/// Requires the dblens-postgres container running with the demo schema.
/// Skipped automatically when PostgreSQL is not reachable.
/// </summary>
public sealed class PostgresIndexAnalysisTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture = fixture;

    private bool ShouldSkip => !_fixture.IsReachable;

    // --- Acceptance criterion 1 ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task AnalyzeIndexes_PublicSchema_ReturnsAtLeastFourFindings()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "public", IncludeUnusedIndexes: false));

        result.Findings.Should().HaveCountGreaterThanOrEqualTo(4,
            "the public demo schema has at least 7 FK-without-index issues");
        result.Summary.TotalFindings.Should().Be(result.Findings.Count);
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task AnalyzeIndexes_PublicSchema_FkWithoutIndex_DetectedOnExpectedTables()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "public", IncludeUnusedIndexes: false));

        var fkFindings = result.Findings.Where(f => f.RuleId == "fk-without-index").ToList();

        // These tables have FK columns with no covering index in the demo schema
        var tablesWithIssue = fkFindings.Select(f => f.Table).ToHashSet();
        tablesWithIssue.Should().Contain("public.addresses", "addresses.user_id has no index");
        tablesWithIssue.Should().Contain("public.orders", "orders.user_id has no index");
        tablesWithIssue.Should().Contain("public.categories", "categories.parent_id has no index");
        tablesWithIssue.Should().Contain("public.products", "products.category_id has no index");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task AnalyzeIndexes_FkWithoutIndex_SqlFix_UsesExpectedPattern()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "public", Tables: ["orders"], IncludeUnusedIndexes: false));

        var finding = result.Findings.First(f =>
            f.RuleId == "fk-without-index" && f.Table == "public.orders");

        finding.SqlFix!.Sql.Should().NotBeNull();
        finding.SqlFix!.Sql.Should().StartWith("CREATE INDEX CONCURRENTLY \"idx_orders_");
        finding.SqlFix!.Sql.Should().Contain("ON \"public\".\"orders\"");
    }

    // --- Acceptance criterion 3 ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task AnalyzeIndexes_DemoGoodSchema_ReturnsNoFindings()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // Use includeUnusedIndexes: false to isolate structural analysis.
        // The demo_good schema was designed with all FKs indexed and no duplicate/redundant indexes.
        var result = await _fixture.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "demo_good", IncludeUnusedIndexes: false));

        result.Findings.Should().BeEmpty(
            "demo_good schema has all FKs covered by indexes with no duplicates or redundancies");
        result.Summary.TotalFindings.Should().Be(0);
        result.Truncated.Should().BeFalse();
    }

    // --- Acceptance criterion 4 ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task AnalyzeIndexes_UnusedIndexFound_IncludesUptimeCaveat()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // idx_orders_status is a deliberately unused non-unique index (never scanned during seeding).
        // unusedIndexMinDays = 0 bypasses the window threshold; the workload is another table's.
        await _fixture.RunWorkloadAsync();
        var result = await _fixture.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(
                Schema: "public",
                Tables: ["orders"],
                IncludeUnusedIndexes: true,
                UnusedIndexMinDays: 0));

        var unusedFindings = result.Findings.Where(f => f.RuleId == "unused-index").ToList();
        unusedFindings.Should().Contain(f =>
            f.Columns != null && f.Columns.Contains("status"),
            "idx_orders_status has never been scanned");

        result.Caveats.Should().NotBeEmpty(
            "unused-index findings must produce an uptime caveat");
        result.Summary.ByRule["unused-index"].Should().BeGreaterThan(0);
    }

    // --- Acceptance criterion 5 ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task AnalyzeIndexes_UnusedIndexMinDays_ExceedsUptimeDays_SkipsRuleAndAddsCaveat()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // int.MaxValue days far exceeds any real DB uptime (~5.9M years).
        var result = await _fixture.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(
                Schema: "public",
                IncludeUnusedIndexes: true,
                UnusedIndexMinDays: int.MaxValue));

        result.Findings.Should().NotContain(f => f.RuleId == "unused-index",
            "unused-index rule should be skipped when uptime < unusedIndexMinDays");
        result.Caveats.Should().Contain(c => c.Contains("unused-index"),
            "a caveat must explain why the rule was skipped");
    }

    // --- Summary structure ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task AnalyzeIndexes_Summary_ContainsAllSeveritiesAndRules()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "public", IncludeUnusedIndexes: false));

        result.Summary.BySeverity.Should().ContainKey("Critical");
        result.Summary.BySeverity.Should().ContainKey("High");
        result.Summary.BySeverity.Should().ContainKey("Medium");
        result.Summary.BySeverity.Should().ContainKey("Low");
        result.Summary.BySeverity.Should().ContainKey("Info");

        result.Summary.ByRule.Should().ContainKey("fk-without-index");
        result.Summary.ByRule.Should().ContainKey("duplicate-index");
        result.Summary.ByRule.Should().ContainKey("redundant-index");
        result.Summary.ByRule.Should().ContainKey("unused-index");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task AnalyzeIndexes_TableFilter_LimitsScope()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(
                Schema: "public",
                Tables: ["orders"],
                IncludeUnusedIndexes: false));

        result.Findings.Should().OnlyContain(f => f.Table == "public.orders",
            "table filter must restrict findings to the specified table");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task AnalyzeIndexes_FkCoveredByCompositeIndex_NofFinding()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // demo_good.employees: department_id is covered by idx_demo_good_employees_department_id.
        // Even though that index could be "covered" by a composite, the single-column index exists.
        // This also tests that valid coverage is not double-flagged.
        var result = await _fixture.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(
                Schema: "demo_good",
                Tables: ["employees"],
                IncludeUnusedIndexes: false));

        result.Findings.Should().NotContain(f =>
            f.RuleId == "fk-without-index" && f.Table == "demo_good.employees",
            "department_id FK is covered by the existing index");
    }
}
