using DbLens.Core.Analysis.Gdpr;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Cross-dialect parity tests for ScanGdprAsync.
/// Both PostgreSQL (public schema) and SQL Server (dbo schema) must produce the same
/// semantic findings: same rule_ids, same table names (modulo schema prefix), same columns.
/// </summary>
public sealed class GdprScanParityTests(PostgresFixture pg, SqlServerFixture ss) :
    IClassFixture<PostgresFixture>,
    IClassFixture<SqlServerFixture>
{
    private readonly PostgresFixture _pg = pg;
    private readonly SqlServerFixture _ss = ss;

    private bool ShouldSkip => !_pg.IsReachable || !_ss.IsReachable;

    [SkippableFact, Trait("Category", "Integration")]
    public async Task PiiFindings_AffectedTableNames_MatchAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.ScanGdprAsync(new ScanGdprRequest(Schema: "public"));
        var ss = await _ss.Inspector!.ScanGdprAsync(new ScanGdprRequest(Schema: "dbo"));

        static IEnumerable<string> PiiTables(ScanGdprResult r) =>
            r.Signals
                .Where(f => f.RuleId != "missing-gdpr-controls")
                .Select(f => f.Table.Split('.')[1])
                .Distinct()
                .OrderBy(t => t);

        PiiTables(pg).Should().Equal(PiiTables(ss),
            "both dialects must detect PII on the same set of tables");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task PiiFindings_AffectedColumns_MatchAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.ScanGdprAsync(new ScanGdprRequest(Schema: "public"));
        var ss = await _ss.Inspector!.ScanGdprAsync(new ScanGdprRequest(Schema: "dbo"));

        static IEnumerable<(string Table, string Col)> PiiPairs(ScanGdprResult r) =>
            r.Signals
                .Where(f => f.RuleId != "missing-gdpr-controls" && f.Columns is { Count: > 0 })
                .Select(f => (Table: f.Table.Split('.')[1], Col: f.Columns![0]))
                .OrderBy(x => x.Table)
                .ThenBy(x => x.Col);

        PiiPairs(pg).Should().Equal(PiiPairs(ss),
            "both dialects must detect the same PII columns");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task Summary_RuleKeys_AreConsistentAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.ScanGdprAsync(new ScanGdprRequest(Schema: "public"));
        var ss = await _ss.Inspector!.ScanGdprAsync(new ScanGdprRequest(Schema: "dbo"));

        pg.Summary.ByRule.Keys.Order().Should().Equal(
            ss.Summary.ByRule.Keys.Order(),
            "both dialects must return the same rule keys");

        pg.Summary.BySeverity.Keys.Order().Should().Equal(
            ss.Summary.BySeverity.Keys.Order(),
            "both dialects must return the same severity keys");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task FindingCount_IsTheSameOnBothDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.ScanGdprAsync(new ScanGdprRequest(Schema: "public"));
        var ss = await _ss.Inspector!.ScanGdprAsync(new ScanGdprRequest(Schema: "dbo"));

        pg.Signals.Count.Should().Be(ss.Signals.Count,
            "the same number of GDPR findings must be produced on both dialects");
    }
}
