using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Gdpr;
using DbLens.Core.Models;
using FluentAssertions;

using static DbLens.Tests.TestSchema;

namespace DbLens.Tests;

/// <summary>
/// Unit tests for GDPR scan coverage — the mechanism that stops an empty result from reading
/// as a clean bill of health. A name-matching scan that finds nothing has two very different
/// meanings, and these tests pin the tool to saying which one it means.
/// </summary>
public sealed class GdprCoverageTests
{
    private static TableDetail Table(string name, params ColumnDetail[] columns) =>
        TestSchema.Table(name, columns, pkColumns: []);

    private static ScanGdprResult Scan(params TableDetail[] tables) =>
        GdprAnalysisEngine.Analyze(tables, new ScanGdprRequest(), DatabaseDialect.PostgreSql);

    // ── Opaque name detection ──────────────────────────────────────────────────

    [Theory]
    [InlineData("c1")]
    [InlineData("c")]
    [InlineData("col_3")]
    [InlineData("fld2")]
    [InlineData("x7")]
    [InlineData("nm")]
    [InlineData("dsc")]
    public void OpaqueNames_AreRecognized(string name)
    {
        GdprCoverageAnalyzer.IsOpaque(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("email")]
    [InlineData("first_name")]
    [InlineData("codice_fiscale")]
    [InlineData("date_of_birth")]
    [InlineData("address_line1")]
    public void MeaningfulNames_AreNotOpaque(string name)
    {
        GdprCoverageAnalyzer.IsOpaque(name).Should().BeFalse();
    }

    // ── Coverage accounting ────────────────────────────────────────────────────

    [Fact]
    public void Coverage_CountsScannedMatchedAndOpaqueColumns()
    {
        var result = Scan(Table("t", Col("email"), Col("c1"), Col("c2"), Col("description")));

        result.Coverage.ColumnsScanned.Should().Be(4);
        result.Coverage.ColumnsMatched.Should().Be(1, "only 'email' matches a PII pattern");
        result.Coverage.ColumnsWithOpaqueNames.Should().Be(2, "'c1' and 'c2' carry no signal");
    }

    [Fact]
    public void Coverage_ReportsInterpretableRatio()
    {
        var result = Scan(Table("t", Col("email"), Col("c1")));

        result.Coverage.InterpretableRatio.Should().Be(0.5);
    }

    [Fact]
    public void Coverage_ListsOpaqueColumnsSoTheyCanBeInspected()
    {
        var result = Scan(Table("t", Col("c1"), Col("c2")));

        result.Coverage.OpaqueColumnExamples.Should().Contain("public.t.c1");
        result.Coverage.OpaqueColumnExamples.Should().Contain("public.t.c2");
    }

    [Fact]
    public void Coverage_IsCompleteWhenEveryNameIsMeaningful()
    {
        var result = Scan(Table("t", Col("email"), Col("description"), Col("country")));

        result.Coverage.ColumnsWithOpaqueNames.Should().Be(0);
        result.Coverage.InterpretableRatio.Should().Be(1.0);
    }

    // ── The false-reassurance guard ────────────────────────────────────────────

    [Fact]
    public void OpaqueColumns_ProduceAnExplicitBlindSpotCaveat()
    {
        var result = Scan(Table("t", Col("c1"), Col("c2"), Col("c3")));

        result.Caveats.Should().Contain(c =>
            c.Contains("no usable signal") && c.Contains("not covered by this result"));
    }

    [Fact]
    public void NoMatches_StatesExplicitlyThatThisIsNotProofOfCompliance()
    {
        var result = Scan(Table("t", Col("widget_colour"), Col("sprocket_count")));

        result.Signals.Should().BeEmpty();
        result.Caveats.Should().Contain(c =>
            c.Contains("NOT a finding that the") && c.Contains("Do not report this result as confirmation"));
    }

    [Fact]
    public void NoMatches_CaveatMentionsUncoveredLanguages()
    {
        var result = Scan(Table("t", Col("widget_colour")));

        result.Caveats.Should().Contain(c => c.Contains("language the dictionaries do not cover"));
    }

    [Fact]
    public void Matches_DoNotSuppressTheOpaqueColumnCaveat()
    {
        // A scan that found something can still be blind to most of the table.
        var result = Scan(Table("t", Col("email"), Col("c1"), Col("c2")));

        result.Signals.Should().NotBeEmpty();
        result.Caveats.Should().Contain(c => c.Contains("no usable signal"));
    }

    [Fact]
    public void EmptyScan_DoesNotClaimCoverageItDoesNotHave()
    {
        var result = GdprAnalysisEngine.Empty();

        result.Coverage.ColumnsScanned.Should().Be(0);
        result.Signals.Should().BeEmpty();
    }

    // ── Facts and conjectures are held apart ───────────────────────────────────

    [Fact]
    public void EveryPiiResult_IsReportedAsASignalNeverAsAFinding()
    {
        var result = Scan(Table("users", Col("email"), Col("fiscal_code"), Col("phone")));

        result.Signals.Should().NotBeEmpty();
        result.Findings.Should().BeEmpty(
            "matching a column name is never evidence that the column holds personal data");
    }

    [Fact]
    public void EverySignal_IsMarkedAsInferredEvidence()
    {
        var result = Scan(Table("users", Col("email"), Col("fiscal_code")));

        result.Signals.Should().OnlyContain(f => f.Evidence == Evidence.Inferred);
    }

    [Fact]
    public void Summary_SeparatesFindingAndSignalCounts()
    {
        var result = Scan(Table("users", Col("email")));

        result.Summary.TotalFindings.Should().Be(0);
        result.Summary.TotalSignals.Should().Be(result.Signals.Count);
    }
}
