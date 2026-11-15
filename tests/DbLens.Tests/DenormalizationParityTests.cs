using DbLens.Core.Analysis.Denormalization;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Cross-dialect parity tests for analyze_denormalization.
/// The rules are dialect-independent — they read table structure only — so both dialects
/// must produce the same findings for the equivalent schema.
/// Skipped automatically when either database is not reachable.
/// </summary>
public sealed class DenormalizationParityTests(PostgresFixture pg, SqlServerFixture ss) :
    IClassFixture<PostgresFixture>,
    IClassFixture<SqlServerFixture>
{
    private readonly PostgresFixture _pg = pg;
    private readonly SqlServerFixture _ss = ss;

    private bool ShouldSkip => !_pg.IsReachable || !_ss.IsReachable;


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeDenormalization_RuleIds_MatchAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: "public"));
        var ss = await _ss.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: "dbo"));

        var pgRules = pg.Signals.Select(f => f.RuleId).Distinct().OrderBy(r => r).ToList();
        var ssRules = ss.Signals.Select(f => f.RuleId).Distinct().OrderBy(r => r).ToList();

        pgRules.Should().BeEquivalentTo(ssRules,
            "both dialects should emit the same set of rule IDs for the equivalent schema");
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeDenormalization_TableAndRulePairs_MatchAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: "public"));
        var ss = await _ss.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: "dbo"));

        var pgPairs = pg.Signals
            .Select(f => (Table: f.Table.Split('.')[1], f.RuleId))
            .Distinct()
            .OrderBy(p => p.Table).ThenBy(p => p.RuleId)
            .ToList();

        var ssPairs = ss.Signals
            .Select(f => (Table: f.Table.Split('.')[1], f.RuleId))
            .Distinct()
            .OrderBy(p => p.Table).ThenBy(p => p.RuleId)
            .ToList();

        pgPairs.Should().BeEquivalentTo(ssPairs,
            "the rules read structure only, so bare table names and rule IDs must line up");
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeDenormalization_DemoGood_CleanOnBothDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: "demo_good"));
        var ss = await _ss.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: "demo_good"));

        pg.Signals.Should().BeEmpty();
        ss.Signals.Should().BeEmpty();
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeDenormalization_RepeatingGroupColumns_MatchAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: "public", Tables: ["customer_profiles"]));
        var ss = await _ss.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: "dbo", Tables: ["customer_profiles"]));

        var pgColumns = pg.Signals
            .Where(f => f.RuleId == DbLens.Core.Analysis.DenormalizationRuleIds.RepeatingGroup)
            .SelectMany(f => f.Columns ?? [])
            .OrderBy(c => c)
            .ToList();

        var ssColumns = ss.Signals
            .Where(f => f.RuleId == DbLens.Core.Analysis.DenormalizationRuleIds.RepeatingGroup)
            .SelectMany(f => f.Columns ?? [])
            .OrderBy(c => c)
            .ToList();

        pgColumns.Should().BeEquivalentTo(ssColumns);
    }
}
