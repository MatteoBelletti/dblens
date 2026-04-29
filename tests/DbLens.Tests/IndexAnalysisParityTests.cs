using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Indexes;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Cross-dialect parity tests for AnalyzeIndexesAsync.
/// Both PostgreSQL (public schema) and SQL Server (dbo schema) must produce the same
/// semantic findings: same rule_ids, same table names (modulo schema prefix), same columns.
/// Allowed differences: constraint/index names, schema prefixes (public vs dbo).
/// </summary>
public sealed class IndexAnalysisParityTests :
    IClassFixture<PostgresFixture>,
    IClassFixture<SqlServerFixture>
{
    private readonly PostgresFixture _pg;
    private readonly SqlServerFixture _ss;

    public IndexAnalysisParityTests(PostgresFixture pg, SqlServerFixture ss)
    {
        _pg = pg;
        _ss = ss;
    }

    private bool ShouldSkip => !_pg.IsReachable || !_ss.IsReachable;

    [Fact, Trait("Category", "Integration")]
    public async Task FkWithoutIndex_AffectedTableNames_MatchAcrossDialects()
    {
        if (ShouldSkip) return;

        var pg = await _pg.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "public", IncludeUnusedIndexes: false));
        var ss = await _ss.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "dbo", IncludeUnusedIndexes: false));

        // Strip schema prefix to get bare table names for comparison
        var pgTables = pg.Findings
            .Where(f => f.RuleId == "fk-without-index")
            .Select(f => f.Table.Split('.')[1])
            .OrderBy(t => t)
            .ToList();
        var ssTables = ss.Findings
            .Where(f => f.RuleId == "fk-without-index")
            .Select(f => f.Table.Split('.')[1])
            .OrderBy(t => t)
            .ToList();

        pgTables.Should().Equal(ssTables,
            "both dialects must detect fk-without-index on the same tables");
    }

    [Fact, Trait("Category", "Integration")]
    public async Task FkWithoutIndex_AffectedColumns_MatchAcrossDialects()
    {
        if (ShouldSkip) return;

        var pg = await _pg.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "public", IncludeUnusedIndexes: false));
        var ss = await _ss.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "dbo", IncludeUnusedIndexes: false));

        // Build (bare-table, first-column) tuples for comparison
        static IEnumerable<(string Table, string Col)> ExtractPairs(IReadOnlyList<Finding> findings) =>
            findings
                .Where(f => f.RuleId == "fk-without-index" && f.Columns is { Count: > 0 })
                .Select(f => (Table: f.Table.Split('.')[1], Col: f.Columns![0]))
                .OrderBy(x => x.Table)
                .ThenBy(x => x.Col);

        var pgPairs = ExtractPairs(pg.Findings).ToList();
        var ssPairs = ExtractPairs(ss.Findings).ToList();

        pgPairs.Should().Equal(ssPairs,
            "both dialects must report the same FK columns as missing index coverage");
    }

    [Fact, Trait("Category", "Integration")]
    public async Task FkWithoutIndex_FindingCount_IsTheSameOnBothDialects()
    {
        if (ShouldSkip) return;

        var pg = await _pg.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "public", IncludeUnusedIndexes: false));
        var ss = await _ss.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "dbo", IncludeUnusedIndexes: false));

        var pgCount = pg.Findings.Count(f => f.RuleId == "fk-without-index");
        var ssCount = ss.Findings.Count(f => f.RuleId == "fk-without-index");

        pgCount.Should().Be(ssCount,
            "the same number of FK-without-index findings must be produced on both dialects");
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DemoGoodSchema_NoFindings_OnBothDialects()
    {
        if (ShouldSkip) return;

        var pg = await _pg.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "demo_good", IncludeUnusedIndexes: false));
        var ss = await _ss.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "demo_good", IncludeUnusedIndexes: false));

        pg.Findings.Should().BeEmpty("demo_good schema (PG) must have 0 findings");
        ss.Findings.Should().BeEmpty("demo_good schema (SS) must have 0 findings");
    }

    [Fact, Trait("Category", "Integration")]
    public async Task Summary_BySeverityKeys_AreConsistentAcrossDialects()
    {
        if (ShouldSkip) return;

        var pg = await _pg.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "public", IncludeUnusedIndexes: false));
        var ss = await _ss.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(Schema: "dbo", IncludeUnusedIndexes: false));

        pg.Summary.BySeverity.Keys.Order().Should().Equal(
            ss.Summary.BySeverity.Keys.Order(),
            "both dialects must return the same severity keys");

        pg.Summary.ByRule.Keys.Order().Should().Equal(
            ss.Summary.ByRule.Keys.Order(),
            "both dialects must return the same rule keys");
    }

    [Fact, Trait("Category", "Integration")]
    public async Task UnusedIndexRule_SkippedByCaveat_OnBothDialects()
    {
        if (ShouldSkip) return;

        var pg = await _pg.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(
                Schema: "public", IncludeUnusedIndexes: true, UnusedIndexMinDays: int.MaxValue));
        var ss = await _ss.Inspector!.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(
                Schema: "dbo", IncludeUnusedIndexes: true, UnusedIndexMinDays: int.MaxValue));

        pg.Findings.Should().NotContain(f => f.RuleId == "unused-index");
        ss.Findings.Should().NotContain(f => f.RuleId == "unused-index");
        pg.Caveats.Should().NotBeEmpty();
        ss.Caveats.Should().NotBeEmpty();
    }
}
