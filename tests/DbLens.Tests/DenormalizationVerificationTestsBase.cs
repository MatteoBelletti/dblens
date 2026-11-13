using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Denormalization;
using DbLens.Core.Analysis.Denormalization.Verification;
using DbLens.Core.Analysis.Verification;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for signal verification, run against every dialect.
///
/// The seed fixtures at the end of db/*/02-seed.sql are identical row for row on both engines, so
/// the expected counts are exact and shared: a verification feature whose tests cannot tell 3 from
/// 8 is not testing the measurement. Skipped automatically when the database is not reachable.
/// </summary>
public abstract class DenormalizationVerificationTestsBase(IDatabaseFixture fixture)
{
    protected IDatabaseFixture Fixture { get; } = fixture;

    protected bool ShouldSkip => !Fixture.IsReachable;

    protected Task<AnalyzeDenormalizationResult> VerifyAsync(int maxQueries = 20) =>
        Fixture.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(
                Schema: Fixture.UserSchema,
                VerifySignals: true,
                MaxVerificationQueries: maxQueries,
                // The demo tables are tiny but their statistics may not be collected yet;
                // a zero floor keeps the probes eligible regardless.
                MaxVerifiableTableRows: long.MaxValue));

    protected static VerificationOutcome Outcome(Finding f) =>
        (VerificationOutcome)f.Metadata!["verification"];

    protected static Finding ForRule(AnalyzeDenormalizationResult r, string ruleId, string column) =>
        r.Findings.Concat(r.Signals).Single(f =>
            f.RuleId == ruleId && (f.Columns ?? []).Contains(column));

    // ── Opt-in ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task WithoutVerification_NothingIsExecutedAndFindingsStaysEmpty()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Fixture.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: Fixture.UserSchema));

        result.Verification.Should().BeNull(
            "a null Verification block distinguishes 'not verified' from 'verified and found nothing'");
        result.Findings.Should().BeEmpty();
        result.Signals.Should().NotBeEmpty();
    }

    // ── Exact measured counts ──────────────────────────────────────────────────

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task CachedCount_MeasuresExactlyThreeDivergingProfiles()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await VerifyAsync();
        var outcome = Outcome(ForRule(result, DenormalizationRuleIds.DerivedColumn, "order_count"));

        outcome.Status.Should().Be("Confirmed");
        outcome.RowsCompared.Should().Be(10);
        outcome.RowsDiverging.Should().Be(3);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task CopiedColumn_MeasuresTheProfileToUserPair()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await VerifyAsync();
        var outcome = Outcome(ForRule(result, DenormalizationRuleIds.DuplicatedColumn, "fiscal_code"));

        // fiscal_code lives on three tables; the probe measures the first FK-linked pair,
        // customer_profiles -> users, and its title must say so.
        outcome.Status.Should().Be("Confirmed");
        outcome.RowsCompared.Should().Be(10);
        outcome.RowsDiverging.Should().Be(9);
        outcome.Measurement.Should().Contain("customer_profiles.fiscal_code").And.Contain("users.fiscal_code");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task MultiValueColumn_MeasuresExactlyFourDelimitedValues()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await VerifyAsync();
        var outcome = Outcome(ForRule(result, DenormalizationRuleIds.MultiValueColumn, "tags"));

        outcome.Status.Should().Be("Confirmed");
        outcome.RowsCompared.Should().Be(10);
        outcome.RowsDiverging.Should().Be(4);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task RepeatingGroup_MeasuresExactlyTwoFullyOccupiedRows()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await VerifyAsync();
        var outcome = Outcome(ForRule(result, DenormalizationRuleIds.RepeatingGroup, "phone1"));

        outcome.Status.Should().Be("Confirmed");
        outcome.RowsCompared.Should().Be(10);
        outcome.RowsDiverging.Should().Be(2);
    }

    // ── Promotion actually happens ─────────────────────────────────────────────

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ConfirmedSignals_BecomeMeasuredFindings()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await VerifyAsync();

        result.Findings.Should().NotBeEmpty(
            "verification exists to move measured signals out of Signals");
        result.Findings.Should().OnlyContain(f => f.Evidence == Evidence.Measured);
        result.Summary.PromotedFromSignals.Should().BeGreaterThan(0);
        result.Verification!.Confirmed.Should().BeGreaterThan(0);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task PromotedFindings_StateTheMeasurementAndTheSnapshotCaveat()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await VerifyAsync();
        var confirmed = result.Findings
            .Where(f => Outcome(f).Status == "Confirmed")
            .ToList();

        confirmed.Should().NotBeEmpty();
        confirmed.Should().OnlyContain(f => f.Description.Contains("MEASURED:"));
        confirmed.Should().OnlyContain(f => f.Description.Contains("WHAT THIS MAY MEAN:"));
        confirmed.Should().OnlyContain(f =>
            f.Description.Contains("does not establish that the difference is a defect"));
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Verified_SwitchesToTheDisclaimerThatAdmitsQueriesRan()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await VerifyAsync();

        result.Disclaimer.Should().Contain("Only counts were read");
        result.Disclaimer.Should().NotBe(DenormalizationAnalysisEngine.Disclaimer);
    }

    // ── demo_good stays clean ──────────────────────────────────────────────────

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task DemoGood_ProducesNothingEvenWithVerificationOn()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Fixture.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: "demo_good", VerifySignals: true));

        result.Signals.Should().BeEmpty();
        result.Findings.Should().BeEmpty(
            "verification must not manufacture findings on a clean schema");
    }

    // ── Budgets ────────────────────────────────────────────────────────────────

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task QueryBudget_IsHonouredAndTheRestStaySignals()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await VerifyAsync(maxQueries: 1);

        result.Verification!.QueriesExecuted.Should().BeLessThanOrEqualTo(1);
        result.Verification.NotAttempted.Should().BeGreaterThan(0);
        result.Caveats.Should().Contain(c => c.Contains("query budget"));
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task VerificationSummary_CountsEveryOutcomeKey()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await VerifyAsync();

        foreach (var status in Enum.GetValues<VerificationStatus>())
            result.Verification!.ByOutcome.Should().ContainKey(status.ToString());
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task NoProbeFailures_AgainstTheDemoSchema()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await VerifyAsync();

        result.Verification!.Failed.Should().Be(0,
            "every generated probe must be valid SQL the demo role can run");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task SumFamilyAggregate_IsNeverVerified()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // orders.total is a monetary sum; comparing it against COUNT(order_items) would
        // disagree on nearly every row and publish a measured falsehood.
        var result = await Fixture.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(
                Schema: Fixture.UserSchema, Tables: ["orders"], VerifySignals: true));

        var total = result.Signals.SingleOrDefault(f =>
            f.RuleId == DenormalizationRuleIds.DerivedColumn && (f.Columns ?? []).Contains("total"));

        total.Should().NotBeNull("orders.total is still flagged as a heuristic");
        total!.Evidence.Should().Be(Evidence.Inferred, "and it must stay a conjecture");
        total.SqlFix.Should().BeNull();
    }
}
