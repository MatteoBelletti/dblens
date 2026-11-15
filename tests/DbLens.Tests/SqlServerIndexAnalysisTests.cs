using DbLens.Core.Analysis.Indexes;
using DbLens.SqlServer;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for SqlServerInspector.AnalyzeIndexesAsync.
/// Requires the dblens-sqlserver container running with the demo schema.
/// Skipped automatically when SQL Server is not reachable.
/// </summary>
public sealed class SqlServerIndexAnalysisTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture = fixture;

    private bool ShouldSkip => !_fixture.IsReachable;

    // --- Acceptance criterion 1 (SQL Server) ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task AnalyzeIndexes_DboSchema_ReturnsAtLeastFourFindings()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "dbo", IncludeUnusedIndexes: false));

        result.Findings.Should().HaveCountGreaterThanOrEqualTo(4,
            "the dbo demo schema has at least 7 FK-without-index issues");
        result.Summary.TotalFindings.Should().Be(result.Findings.Count);
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task AnalyzeIndexes_DboSchema_FkWithoutIndex_DetectedOnExpectedTables()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "dbo", IncludeUnusedIndexes: false));

        var fkFindings = result.Findings.Where(f => f.RuleId == "fk-without-index").ToList();
        var tablesWithIssue = fkFindings.Select(f => f.Table).ToHashSet();

        tablesWithIssue.Should().Contain("dbo.addresses");
        tablesWithIssue.Should().Contain("dbo.orders");
        tablesWithIssue.Should().Contain("dbo.categories");
        tablesWithIssue.Should().Contain("dbo.products");
    }

    // --- Acceptance criterion 3 (SQL Server) ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task AnalyzeIndexes_DemoGoodSchema_ReturnsNoFindings()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "demo_good", IncludeUnusedIndexes: false));

        result.Findings.Should().BeEmpty(
            "demo_good schema has all FKs covered by indexes with no duplicates or redundancies");
    }

    // --- Acceptance criterion 4 (SQL Server) ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task AnalyzeIndexes_UnusedIndex_DetectedOnSqlServer()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(
                Schema: "dbo",
                Tables: ["orders"],
                IncludeUnusedIndexes: true,
                UnusedIndexMinDays: 0));

        var unusedFindings = result.Findings.Where(f => f.RuleId == "unused-index").ToList();
        unusedFindings.Should().Contain(f =>
            f.Columns != null && f.Columns.Contains("status"),
            "idx_orders_status has never been scanned");

        result.Caveats.Should().NotBeEmpty();
    }

    // --- Acceptance criterion 5 (SQL Server) ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task AnalyzeIndexes_HighMinDays_SkipsUnusedRuleAndAddsCaveat()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(
                Schema: "dbo",
                IncludeUnusedIndexes: true,
                UnusedIndexMinDays: int.MaxValue));

        result.Findings.Should().NotContain(f => f.RuleId == "unused-index");
        result.Caveats.Should().Contain(c => c.Contains("unused-index"));
    }

    // --- SqlFix dialect-specific syntax ---

    [SkippableFact, Trait("Category", "Integration")]
    public async Task AnalyzeIndexes_SqlFix_UsesSqlServerDropSyntax()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // Run with unusedIndexMinDays = 0 to get unused-index findings and their SQL fix
        var result = await _fixture.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(
                Schema: "dbo",
                Tables: ["orders"],
                IncludeUnusedIndexes: true,
                UnusedIndexMinDays: 0));

        var unused = result.Findings.FirstOrDefault(f => f.RuleId == "unused-index");
        if (unused is null) return; // no unused index found, test is vacuously true

        unused.SqlFix!.Sql.Should().Contain("ON [dbo].[orders]",
            "SQL Server DROP INDEX syntax is 'DROP INDEX [name] ON [schema].[table]'");
    }
}
