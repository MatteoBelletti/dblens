using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Cascade;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Cross-dialect parity tests for check_cascade_paths.
/// Verifies that Postgres and SQL Server produce equivalent findings for the same schema.
/// Skipped automatically when either database is not reachable.
/// </summary>
public sealed class CascadeParityTests(PostgresFixture pg, SqlServerFixture ss) :
    IClassFixture<PostgresFixture>,
    IClassFixture<SqlServerFixture>
{
    private readonly PostgresFixture _pg = pg;
    private readonly SqlServerFixture _ss = ss;

    private bool ShouldSkip => !_pg.IsReachable || !_ss.IsReachable;


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_Users_FindingRuleIds_MatchAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));
        var ss = await _ss.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("dbo.users"));

        var pgRuleIds = pg.Findings
            .Select(f => f.RuleId)
            .OrderBy(r => r)
            .ToList();

        var ssRuleIds = ss.Findings
            .Select(f => f.RuleId)
            .OrderBy(r => r)
            .ToList();

        pgRuleIds.Should().BeEquivalentTo(ssRuleIds,
            "both dialects should emit the same set of rule IDs for the users table");
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_Users_FindingSeverities_MatchAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));
        var ss = await _ss.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("dbo.users"));

        var pgBySeverity = pg.Summary.BySeverity;
        var ssBySeverity = ss.Summary.BySeverity;

        pgBySeverity.Should().BeEquivalentTo(ssBySeverity,
            "both dialects should count findings by severity identically");
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_Users_FindingTables_MatchAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));
        var ss = await _ss.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("dbo.users"));

        // Strip schema prefix for comparison (public.orders vs dbo.orders)
        var pgTables = pg.Findings
            .Where(f => f.RuleId != CascadeRuleIds.Summary)
            .Select(f => f.Table.Split('.')[1])
            .OrderBy(t => t)
            .ToList();

        var ssTables = ss.Findings
            .Where(f => f.RuleId != CascadeRuleIds.Summary)
            .Select(f => f.Table.Split('.')[1])
            .OrderBy(t => t)
            .ToList();

        pgTables.Should().BeEquivalentTo(ssTables,
            "both dialects should identify the same bare table names in findings");
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_Categories_SelfReference_DiffersByDialectAsDocumented()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.categories"));
        var ss = await _ss.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("dbo.categories"));

        // Not a parity gap: SQL Server cannot express a self-referencing ON DELETE CASCADE
        // (Msg 1785), so its demo schema uses NO ACTION. The tool must report what each schema
        // actually declares rather than paper over the difference.
        pg.Findings.Should().Contain(f => f.RuleId == CascadeRuleIds.SelfReferencing);
        ss.Findings.Should().NotContain(f => f.RuleId == CascadeRuleIds.SelfReferencing);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_Users_MaxDepth_MatchAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));
        var ss = await _ss.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("dbo.users"));

        pg.Summary.MaxDepth.Should().Be(ss.Summary.MaxDepth,
            "both dialects should reach the same maximum cascade depth");
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task CheckCascadePaths_Users_PathCount_MatchAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("public.users"));
        var ss = await _ss.Inspector!.CheckCascadePathsAsync(
            new CheckCascadePathsRequest("dbo.users"));

        pg.Summary.TotalPaths.Should().Be(ss.Summary.TotalPaths,
            "both dialects should find the same number of cascade paths from users");
    }
}
