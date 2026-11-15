using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Denormalization;
using DbLens.Core.Analysis.Denormalization.Verification;
using DbLens.Core.Analysis.Verification;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Cross-dialect parity for signal verification.
///
/// The seed fixtures are identical row for row on both engines, so unlike fragmentation — where
/// the numbers are physical and legitimately differ — here the measured counts and the verdicts
/// must match exactly. A divergence means one dialect's probe SQL is wrong.
/// Skipped automatically when either database is not reachable.
/// </summary>
public sealed class DenormalizationVerificationParityTests(PostgresFixture pg, SqlServerFixture ss) :
    IClassFixture<PostgresFixture>,
    IClassFixture<SqlServerFixture>
{
    private readonly PostgresFixture _pg = pg;
    private readonly SqlServerFixture _ss = ss;

    private bool ShouldSkip => !_pg.IsReachable || !_ss.IsReachable;

    private static AnalyzeDenormalizationRequest Request(string schema) =>
        new(Schema: schema, VerifySignals: true, MaxVerifiableTableRows: long.MaxValue);

    /// <summary>(rule, bare column, status, compared, diverging) for every measured result.</summary>
    private static List<(string Rule, string Column, string Status, long? Compared, long? Diverging)>
        Measurements(AnalyzeDenormalizationResult r) =>
        [.. r.Findings.Concat(r.Signals)
            .Where(f => f.Metadata is not null && f.Metadata.ContainsKey("verification"))
            .Select(f =>
            {
                var o = (VerificationOutcome)f.Metadata!["verification"];
                return (f.RuleId, string.Join(",", f.Columns ?? []), o.Status, o.RowsCompared, o.RowsDiverging);
            })
            .OrderBy(x => x.RuleId).ThenBy(x => x.Item2, StringComparer.Ordinal)];

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task MeasuredCountsAndVerdicts_MatchExactlyAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.AnalyzeDenormalizationAsync(Request("public"));
        var ss = await _ss.Inspector!.AnalyzeDenormalizationAsync(Request("dbo"));

        Measurements(pg).Should().BeEquivalentTo(Measurements(ss),
            "the seed data is identical, so any difference means one dialect's probe SQL is wrong");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task PromotionCounts_MatchAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.AnalyzeDenormalizationAsync(Request("public"));
        var ss = await _ss.Inspector!.AnalyzeDenormalizationAsync(Request("dbo"));

        ss.Summary.PromotedFromSignals.Should().Be(pg.Summary.PromotedFromSignals);
        ss.Verification!.Confirmed.Should().Be(pg.Verification!.Confirmed);
        ss.Verification.Refuted.Should().Be(pg.Verification.Refuted);
        ss.Verification.Failed.Should().Be(pg.Verification.Failed);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task VerifiedFindings_CarryMeasuredEvidenceOnBothDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.AnalyzeDenormalizationAsync(Request("public"));
        var ss = await _ss.Inspector!.AnalyzeDenormalizationAsync(Request("dbo"));

        pg.Findings.Should().OnlyContain(f => f.Evidence == Evidence.Measured);
        ss.Findings.Should().OnlyContain(f => f.Evidence == Evidence.Measured);
        pg.Signals.Should().OnlyContain(f => f.Evidence == Evidence.Inferred);
        ss.Signals.Should().OnlyContain(f => f.Evidence == Evidence.Inferred);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task DemoGood_StaysCleanOnBothDialectsWithVerificationOn()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.AnalyzeDenormalizationAsync(Request("demo_good"));
        var ss = await _ss.Inspector!.AnalyzeDenormalizationAsync(Request("demo_good"));

        pg.Findings.Should().BeEmpty();
        pg.Signals.Should().BeEmpty();
        ss.Findings.Should().BeEmpty();
        ss.Signals.Should().BeEmpty();
    }
}
