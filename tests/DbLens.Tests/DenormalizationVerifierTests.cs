using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Denormalization;
using DbLens.Core.Analysis.Denormalization.Verification;
using DbLens.Core.Analysis.Verification;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Unit tests for DenormalizationVerifier — the pure pass that turns measurements into
/// reclassified findings. No database, no dialect. Every promotion decision in the product
/// lives here, so this is where the product's honesty is enforced.
/// </summary>
public sealed class DenormalizationVerifierTests
{
    // ── Builders ───────────────────────────────────────────────────────────────

    private static Finding Signal(
        string ruleId = DenormalizationRuleIds.DerivedColumn,
        string table = "public.customers",
        string column = "order_count",
        Severity severity = Severity.Medium) =>
        new(ruleId, severity, FindingCategory.Structure, 0.6, Evidence.Inferred, table, [column],
            $"Column '{column}' looks like a cached aggregate",
            "A stored aggregate silently drifts from its source.",
            null, new Dictionary<string, object> { ["kind"] = "aggregate" });

    private static AnalyzeDenormalizationResult Unverified(params Finding[] signals) =>
        new(
            Findings: [],
            Signals: signals,
            Summary: new DenormalizationSummary(0, signals.Length, 1,
                Enum.GetValues<Severity>().ToDictionary(s => s.ToString(), _ => 0),
                DenormalizationRuleIds.All.ToDictionary(r => r, _ => 0)),
            Caveats: ["3 result(s) are reported under Signals rather than Findings."],
            Truncated: false,
            Disclaimer: DenormalizationAnalysisEngine.Disclaimer);

    private static VerificationProbe Probe(Finding signal) =>
        new(new VerificationTarget(signal.RuleId, signal.Table, signal.Columns ?? []),
            VerificationProbeKind.CachedCountDivergence,
            "SELECT COUNT(*) AS compared_rows, 0 AS divergent_rows FROM t",
            "rows whose stored count differs from a live count of child rows",
            1000);

    private static AnalyzeDenormalizationResult Verify(
        Finding signal, VerificationMeasurement measurement) =>
        DenormalizationVerifier.Apply(
            Unverified(signal), [measurement], new AnalyzeDenormalizationRequest(VerifySignals: true));

    // ── Confirmed ──────────────────────────────────────────────────────────────

    [Fact]
    public void Confirmed_MovesTheSignalIntoFindingsAsMeasured()
    {
        var signal = Signal();
        var result = Verify(signal, VerificationMeasurement.Measured(Probe(signal), 8300, 1240, 12));

        result.Findings.Should().ContainSingle();
        result.Signals.Should().BeEmpty();

        var finding = result.Findings.Single();
        finding.Evidence.Should().Be(Evidence.Measured);
        finding.Confidence.Should().Be(1.0);
        result.Summary.PromotedFromSignals.Should().Be(1);
    }

    [Fact]
    public void Confirmed_TitleAssertsTheMeasurementNotTheDiagnosis()
    {
        var signal = Signal();
        var result = Verify(signal, VerificationMeasurement.Measured(Probe(signal), 8300, 1240, 12));

        var title = result.Findings.Single().Title;

        // Confidence 1.0 is only defensible because the Title states the count and nothing else.
        title.Should().Contain("1,240");
        title.Should().Contain("8,300");
        title.Should().NotContain("looks like");
    }

    [Fact]
    public void Confirmed_DescriptionSeparatesMeasurementFromInterpretation()
    {
        var signal = Signal();
        var result = Verify(signal, VerificationMeasurement.Measured(Probe(signal), 8300, 1240, 12));

        var description = result.Findings.Single().Description;

        description.Should().Contain("MEASURED:");
        description.Should().Contain("WHAT THIS MAY MEAN:");
    }

    [Fact]
    public void Confirmed_AlwaysStatesThatASnapshotLooksIdentical()
    {
        // The demo data itself contains a legitimate snapshot. If the text ever reads as a
        // verdict, users will "fix" correct data.
        var signal = Signal();
        var result = Verify(signal, VerificationMeasurement.Measured(Probe(signal), 8300, 1240, 12));

        result.Findings.Single().Description.Should()
            .Contain("snapshot").And.Contain("does not establish that the difference is a defect");
    }

    [Fact]
    public void Confirmed_CarriesTheNumbersAndTheQueryInMetadata()
    {
        var signal = Signal();
        var result = Verify(signal, VerificationMeasurement.Measured(Probe(signal), 8300, 1240, 12));

        var outcome = result.Findings.Single().Metadata!["verification"]
            .Should().BeOfType<VerificationOutcome>().Subject;

        outcome.RowsCompared.Should().Be(8300);
        outcome.RowsDiverging.Should().Be(1240);
        outcome.Status.Should().Be("Confirmed");
        outcome.Query.Should().NotBeNullOrWhiteSpace("the result must be reproducible by hand");
        outcome.NotVerifiedReason.Should().BeNull();
    }

    // ── Refuted ────────────────────────────────────────────────────────────────

    [Fact]
    public void Refuted_IsAlsoAMeasuredFindingButOnlyInfo()
    {
        var signal = Signal();
        var result = Verify(signal, VerificationMeasurement.Measured(Probe(signal), 12400, 0, 9));

        var finding = result.Findings.Should().ContainSingle().Subject;
        finding.Evidence.Should().Be(Evidence.Measured);
        finding.Severity.Should().Be(Severity.Info);
        finding.Title.Should().Contain("no divergence").And.Contain("12,400");
    }

    [Fact]
    public void Refuted_OnASmallSampleSaysSo()
    {
        var signal = Signal();
        var small = Verify(signal, VerificationMeasurement.Measured(Probe(signal), 7, 0, 1));
        var large = Verify(signal, VerificationMeasurement.Measured(Probe(signal), 12400, 0, 9));

        // "0 of 7 rows disagree" is true at any N; only the inference from it is weak.
        small.Findings.Single().Description.Should().Contain("small sample");
        large.Findings.Single().Description.Should().NotContain("small sample");
    }

    // ── The failure that matters most ──────────────────────────────────────────

    [Fact]
    public void ZeroRowsCompared_IsNeverReportedAsRefuted()
    {
        var signal = Signal();
        var measurement = VerificationMeasurement.Measured(Probe(signal), 0, 0, 3);

        measurement.Status.Should().Be(VerificationStatus.Inconclusive);

        var result = Verify(signal, measurement);

        result.Findings.Should().BeEmpty("an empty table cannot disprove anything");
        result.Signals.Should().ContainSingle();
        result.Signals.Single().Evidence.Should().Be(Evidence.Inferred);
    }

    [Fact]
    public void ZeroRowsCompared_AddsAnExplicitCaveat()
    {
        var signal = Signal();
        var result = Verify(signal, VerificationMeasurement.Measured(Probe(signal), 0, 0, 3));

        result.Caveats.Should().Contain(c =>
            c.Contains("compared no rows") && c.Contains("NOT as refuted"));
    }

    // ── Failures and skips leave the conjecture untouched ──────────────────────

    [Fact]
    public void FailedProbe_LeavesTheSignalExactlyAsStrongAsBefore()
    {
        var signal = Signal();
        var result = Verify(signal,
            VerificationMeasurement.Failed(Probe(signal), VerificationFailureKind.PermissionDenied, "denied", 4));

        var unchanged = result.Signals.Should().ContainSingle().Subject;
        unchanged.Evidence.Should().Be(Evidence.Inferred);
        unchanged.Confidence.Should().Be(signal.Confidence);
        unchanged.Severity.Should().Be(signal.Severity);
    }

    [Fact]
    public void FailedProbe_ExplainsWhyInMetadataAndCaveats()
    {
        var signal = Signal();
        var result = Verify(signal,
            VerificationMeasurement.Failed(Probe(signal), VerificationFailureKind.PermissionDenied, "denied", 4));

        var outcome = (VerificationOutcome)result.Signals.Single().Metadata!["verification"];
        outcome.NotVerifiedReason.Should().Be("PermissionDenied");
        result.Caveats.Should().Contain(c => c.Contains("PermissionDenied"));
    }

    [Theory]
    [InlineData(VerificationSkipReason.RowCountUnknown, "statistics")]
    [InlineData(VerificationSkipReason.TableTooLarge, "size limit")]
    [InlineData(VerificationSkipReason.QueryBudgetExhausted, "query budget")]
    public void SkippedProbe_StaysASignalAndSaysWhy(VerificationSkipReason reason, string expected)
    {
        var signal = Signal();
        var result = Verify(signal, VerificationMeasurement.Skipped(Probe(signal), reason));

        result.Signals.Should().ContainSingle();
        result.Findings.Should().BeEmpty();
        result.Caveats.Should().Contain(c => c.Contains(expected));
    }

    [Fact]
    public void SignalWithNoMeasurementAtAll_IsPassedThroughUnchanged()
    {
        var signal = Signal();
        var result = DenormalizationVerifier.Apply(
            Unverified(signal), [], new AnalyzeDenormalizationRequest(VerifySignals: true));

        result.Signals.Should().ContainSingle();
        result.Signals.Single().Metadata.Should().NotContainKey("verification");
    }

    // ── Result contract ────────────────────────────────────────────────────────

    [Fact]
    public void Verified_SwitchesToTheDisclaimerThatAdmitsDataWasRead()
    {
        var signal = Signal();
        var result = Verify(signal, VerificationMeasurement.Measured(Probe(signal), 100, 5, 2));

        // The unverified disclaimer says "No row data is read", which stops being true.
        result.Disclaimer.Should().NotBe(DenormalizationAnalysisEngine.Disclaimer);
        result.Disclaimer.Should().Contain("Only counts were read");
    }

    [Fact]
    public void Verified_SummaryCountsEveryOutcome()
    {
        var a = Signal(column: "order_count");
        var b = Signal(column: "item_count", table: "public.baskets");

        var result = DenormalizationVerifier.Apply(
            Unverified(a, b),
            [
                VerificationMeasurement.Measured(Probe(a), 500, 12, 5),
                VerificationMeasurement.Measured(Probe(b), 800, 0, 6),
            ],
            new AnalyzeDenormalizationRequest(VerifySignals: true));

        result.Verification.Should().NotBeNull();
        result.Verification!.Attempted.Should().BeTrue();
        result.Verification.Confirmed.Should().Be(1);
        result.Verification.Refuted.Should().Be(1);
        result.Verification.QueriesExecuted.Should().Be(2);

        foreach (var status in Enum.GetValues<VerificationStatus>())
            result.Verification.ByOutcome.Should().ContainKey(status.ToString());
    }

    [Fact]
    public void Verified_SummaryDictionariesStillCoverEveryRuleAndSeverity()
    {
        var signal = Signal();
        var result = Verify(signal, VerificationMeasurement.Measured(Probe(signal), 500, 12, 5));

        foreach (var rule in DenormalizationRuleIds.All)
            result.Summary.ByRule.Should().ContainKey(rule);

        foreach (var severity in Enum.GetValues<Severity>())
            result.Summary.BySeverity.Should().ContainKey(severity.ToString());
    }

    [Fact]
    public void Verified_DropsTheStaleSignalsAreConjecturesCaveatWhenNoneRemain()
    {
        var signal = Signal();
        var result = Verify(signal, VerificationMeasurement.Measured(Probe(signal), 500, 12, 5));

        result.Signals.Should().BeEmpty();
        result.Caveats.Should().NotContain(c =>
            c.Contains("reported under Signals rather than Findings"));
    }

    [Fact]
    public void RuleIdsAreNeverForked()
    {
        // Verification state is expressed by which list a finding is in and by Evidence,
        // never by inventing a new rule ID that would break ByRule's key contract.
        var signal = Signal();
        var result = Verify(signal, VerificationMeasurement.Measured(Probe(signal), 500, 12, 5));

        result.Findings.Single().RuleId.Should().Be(DenormalizationRuleIds.DerivedColumn);
        DenormalizationRuleIds.All.Should().Contain(result.Findings.Single().RuleId);
    }
}
