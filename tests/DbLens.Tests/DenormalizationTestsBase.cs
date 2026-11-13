using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Denormalization;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for AnalyzeDenormalizationAsync without verification, run against every
/// dialect. The rules read structure only, so the expectations are identical on both engines.
/// Skipped automatically when the database is not reachable.
/// </summary>
public abstract class DenormalizationTestsBase(IDatabaseFixture fixture)
{
    protected IDatabaseFixture Fixture { get; } = fixture;

    private bool ShouldSkip => !Fixture.IsReachable;

    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeDenormalization_DemoGoodSchema_ReturnsNoFindings()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Fixture.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: "demo_good"));

        result.Signals.Should().BeEmpty(
            "demo_good is the clean-design acceptance baseline for every diagnostic rule");
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeDenormalization_CustomerProfiles_DetectsRepeatingGroup()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Fixture.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: Fixture.UserSchema, Tables: ["customer_profiles"]));

        var finding = result.Signals
            .Where(f => f.RuleId == DenormalizationRuleIds.RepeatingGroup)
            .Should().ContainSingle().Subject;

        finding.Columns.Should().BeEquivalentTo(["phone1", "phone2", "phone3"]);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeDenormalization_CustomerProfiles_DetectsDerivedColumns()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Fixture.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: Fixture.UserSchema, Tables: ["customer_profiles"]));

        var derived = result.Signals
            .Where(f => f.RuleId == DenormalizationRuleIds.DerivedColumn)
            .SelectMany(f => f.Columns ?? [])
            .ToList();

        derived.Should().Contain("full_name");
        derived.Should().Contain("order_count");
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeDenormalization_CustomerProfiles_DetectsMultiValueColumn()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Fixture.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: Fixture.UserSchema, Tables: ["customer_profiles"]));

        result.Signals
            .Where(f => f.RuleId == DenormalizationRuleIds.MultiValueColumn)
            .SelectMany(f => f.Columns ?? [])
            .Should().Contain("tags");
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeDenormalization_DetectsFiscalCodeCopiedAcrossFkLinkedTables()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Fixture.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: Fixture.UserSchema));

        result.Signals
            .Where(f => f.RuleId == DenormalizationRuleIds.DuplicatedColumn)
            .SelectMany(f => f.Columns ?? [])
            .Should().Contain("fiscal_code");
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeDenormalization_SummaryContainsAllRuleKeys()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Fixture.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: Fixture.UserSchema));

        foreach (var rule in DenormalizationRuleIds.All)
            result.Summary.ByRule.Should().ContainKey(rule);

        foreach (var severity in Enum.GetValues<Severity>())
            result.Summary.BySeverity.Should().ContainKey(severity.ToString());
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeDenormalization_UnknownSchema_ReturnsEmptyResult()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Fixture.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: "no_such_schema"));

        result.Signals.Should().BeEmpty();
        result.Summary.TotalFindings.Should().Be(0);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task AnalyzeDenormalization_DisclaimerIsAlwaysPresent()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await Fixture.Inspector!.AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(Schema: Fixture.UserSchema));

        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
        result.Disclaimer.Should().Contain("row data");
    }
}
