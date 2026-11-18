using System.Text.Json;
using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Hygiene;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Analysis.Receipts;
using DbLens.Core.Analysis.Verification;
using DbLens.EfCore;
using FluentAssertions;

namespace DbLens.Tests;

public abstract class HygieneTestsBase(IHygieneFixture fixture)
{
    protected IHygieneFixture Fixture { get; } = fixture;

    protected bool ShouldSkip => !Fixture.IsReachable;

    /// <summary>The fixture's counters cover minutes: the tests accept that as enough to read them.</summary>
    protected Task<AnalyzeTableHygieneResult> Analyze(bool verify, bool withModel = false) =>
        Fixture.Inspector!.AnalyzeTableHygieneAsync(new AnalyzeTableHygieneRequest(
            VerifySignals: verify,
            Models: withModel
                ? [SnapshotReader.Read(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EfSnapshots", "ef10", Fixture.Provider))]
                : null,
            UpdateCountersMinDays: 0));

    private static IEnumerable<(string Rule, string Table, string Columns)> Shape(IEnumerable<Finding> findings) =>
        findings.Where(f => f.RuleId != HygieneRuleIds.Summary)
            .Select(f => (f.RuleId, f.Table, string.Join(",", f.Columns ?? [])));

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task WithoutVerification_StatisticsAndNamesGiveSignals_TheCatalogFacts()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Analyze(verify: false);

        Shape(result.Signals).Should().BeEquivalentTo(
        [
            (HygieneRuleIds.ColumnAlwaysNull, "hygiene.subscriptions", "fax_number"),
            (HygieneRuleIds.ColumnSingleValue, "hygiene.subscriptions", "channel"),
            (HygieneRuleIds.ColumnLegacyName, "hygiene.subscriptions", "notes_old"),
            (HygieneRuleIds.UpdatedNotMaintained, "hygiene.subscriptions", "updated_at"),
            (HygieneRuleIds.MissingCreated, "hygiene.stock_levels", ""),
            (HygieneRuleIds.MissingUpdated, "hygiene.stock_levels", ""),
        ]);
        Shape(result.Findings).Should().BeEquivalentTo(
        [
            (HygieneRuleIds.WithoutTimeZone, "hygiene.subscriptions", "created_at"),
            (HygieneRuleIds.WithoutTimeZone, "hygiene.subscriptions", "updated_at"),
        ]);
        result.Signals.Single(f => f.RuleId == HygieneRuleIds.MissingUpdated).Severity.Should().Be(Severity.Medium, "the table is updated");
        result.Verification.Should().BeNull();
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task WithVerification_EachDeliberateDefect_IsMeasured()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Analyze(verify: true);
        var measured = result.Findings.Where(f => f.Evidence == Evidence.Measured).ToList();

        Shape(measured).Should().BeEquivalentTo(
        [
            (HygieneRuleIds.ColumnAlwaysNull, "hygiene.subscriptions", "fax_number"),
            (HygieneRuleIds.ColumnSingleValue, "hygiene.subscriptions", "channel"),
            (HygieneRuleIds.ColumnAbandoned, "hygiene.subscriptions", "coupon_code"),
            (HygieneRuleIds.UpdatedNotMaintained, "hygiene.subscriptions", "updated_at"),
        ], "notes_old is filled on recent rows and no update precedes a creation: those checks are dropped, not reported");
        measured.Single(f => f.RuleId == HygieneRuleIds.ColumnAlwaysNull).Title
            .Should().Be("hygiene.subscriptions.fax_number is NULL in all 3,000 rows");
        measured.Single(f => f.RuleId == HygieneRuleIds.ColumnAbandoned).Title
            .Should().Be("hygiene.subscriptions.coupon_code is empty in all 1,200 recent rows");
        measured.Should().OnlyContain(f => f.Description.Contains("WHAT THIS MAY MEAN:"));
        result.Verification!.Confirmed.Should().Be(4);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task NoValueOfAColumn_EverLeavesTheTool()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        foreach (var verify in new[] { false, true })
            JsonSerializer.Serialize(await Analyze(verify)).Should().NotContainEquivalentOf($"\"{HygieneData.ConstantValue}\"")
                .And.NotContainEquivalentOf($"'{HygieneData.ConstantValue}'")
                .And.NotContainEquivalentOf($" {HygieneData.ConstantValue} ");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task WithTheEfModel_AColumnAddedByHand_IsNotInTheModel()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Analyze(verify: false, withModel: true);

        var finding = result.Findings.Should().ContainSingle(f => f.RuleId == HygieneRuleIds.ColumnNotInModel).Subject;
        finding.Table.Should().Be("shop.users");
        finding.Columns.Should().Equal("loyalty_code");
        ((IReadOnlyDictionary<string, object>)finding.Metadata!["ef"])["entity"].Should().Be("DemoShop.EfCore.User");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task EveryProbe_PassesExplainQuery_AndReceiptReproducesItsCounts()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var inspector = Fixture.Inspector!;
        foreach (var finding in (await Analyze(verify: true)).Findings.Where(f => f.Evidence == Evidence.Measured))
        {
            var outcome = (VerificationOutcome)finding.Metadata!["verification"];

            StatementGuard.IsReadOnlySelect(outcome.Query, inspector.Dialect).Allowed.Should().BeTrue(outcome.Query);
            (await inspector.ExplainQueryAsync(new ExplainQueryRequest(outcome.Query, Analyze: true))).Should().NotBeNull();

            var row = (await inspector.QueryWithReceiptAsync(new QueryWithReceiptRequest(outcome.Query))).Result.Rows.Single();
            Convert.ToInt64(row[0]).Should().Be(outcome.RowsCompared);
            Convert.ToInt64(row[1]).Should().Be(outcome.RowsDiverging);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ACleanSchema_GivesOnlyTheSummary()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Fixture.Inspector!.AnalyzeTableHygieneAsync(new AnalyzeTableHygieneRequest(Schema: "shop", VerifySignals: true));

        result.Findings.Concat(result.Signals).Should().ContainSingle().Which.RuleId.Should().Be(HygieneRuleIds.Summary);
    }
}
