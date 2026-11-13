using DbLens.Core.Analysis;
using DbLens.Core.Analysis.Denormalization;
using DbLens.Core.Models;
using FluentAssertions;

using static DbLens.Tests.TestSchema;

namespace DbLens.Tests;

/// <summary>
/// Unit tests for DenormalizationAnalysisEngine — pure in-memory logic with no database access.
/// </summary>
public sealed class DenormalizationAnalysisTests
{
    // ── Builders ───────────────────────────────────────────────────────────────

    private static AnalyzeDenormalizationResult Analyze(
        IReadOnlyList<TableDetail> tables,
        AnalyzeDenormalizationRequest? request = null) =>
        DenormalizationAnalysisEngine.Analyze(
            tables,
            request ?? new AnalyzeDenormalizationRequest(),
            DatabaseDialect.PostgreSql);

    // Every rule in this tool is a naming heuristic, so results land in Signals, never Findings.
    private static IEnumerable<Finding> OfRule(AnalyzeDenormalizationResult r, string ruleId) =>
        r.Signals.Where(f => f.RuleId == ruleId);

    // ── Empty / summary contract ───────────────────────────────────────────────

    [Fact]
    public void Empty_HasNoFindingsAndFullSummary()
    {
        var result = DenormalizationAnalysisEngine.Empty();

        result.Findings.Should().BeEmpty();
        result.Signals.Should().BeEmpty();
        result.Truncated.Should().BeFalse();
        result.Disclaimer.Should().NotBeNullOrWhiteSpace();
    }

    // ── Facts and conjectures are held apart ───────────────────────────────────

    [Fact]
    public void EveryResult_IsReportedAsASignalNeverAsAFinding()
    {
        var result = Analyze([
            Table("profiles", [
                Col("id", "bigint"), Col("phone1"), Col("phone2"), Col("tags"), Col("full_name"),
            ]),
        ]);

        result.Signals.Should().NotBeEmpty();
        result.Findings.Should().BeEmpty(
            "this tool reads column names, so it can never establish a fact about the data");
    }

    [Fact]
    public void EverySignal_IsMarkedAsInferredEvidence()
    {
        var result = Analyze([
            Table("profiles", [Col("id", "bigint"), Col("phone1"), Col("phone2"), Col("tags")]),
        ]);

        result.Signals.Should().OnlyContain(f => f.Evidence == Evidence.Inferred);
    }

    [Fact]
    public void SignalsPresent_AddsCaveatExplainingTheDistinction()
    {
        var result = Analyze([
            Table("profiles", [Col("id", "bigint"), Col("phone1"), Col("phone2")]),
        ]);

        result.Caveats.Should().Contain(c => c.Contains("hypotheses to confirm"));
    }

    [Fact]
    public void Summary_AlwaysContainsEveryRuleAndSeverityKey()
    {
        var result = Analyze([Table("t", [Col("id", "bigint")])]);

        foreach (var rule in DenormalizationRuleIds.All)
            result.Summary.ByRule.Should().ContainKey(rule);

        foreach (var severity in Enum.GetValues<Severity>())
            result.Summary.BySeverity.Should().ContainKey(severity.ToString());
    }

    [Fact]
    public void Summary_TotalFindingsMatchesFindingCount()
    {
        var result = Analyze([
            Table("profiles", [
                Col("id", "bigint"), Col("phone1"), Col("phone2"), Col("phone3"),
            ]),
        ]);

        result.Summary.TotalFindings.Should().Be(result.Findings.Count);
        result.Summary.TotalSignals.Should().Be(result.Signals.Count);
        result.Summary.TablesAnalyzed.Should().Be(1);
    }

    // ── denorm-repeating-group ─────────────────────────────────────────────────

    [Fact]
    public void RepeatingGroup_DetectsNumberedColumnFamily()
    {
        var result = Analyze([
            Table("profiles", [Col("id", "bigint"), Col("phone1"), Col("phone2"), Col("phone3")]),
        ]);

        var finding = OfRule(result, DenormalizationRuleIds.RepeatingGroup).Should().ContainSingle().Subject;
        finding.Columns.Should().BeEquivalentTo(["phone1", "phone2", "phone3"]);
        finding.Table.Should().Be("public.profiles");
    }

    [Fact]
    public void RepeatingGroup_ThreeMembersScoresHigherThanTwo()
    {
        var two = Analyze([Table("a", [Col("id", "bigint"), Col("addr1"), Col("addr2")])]);
        var three = Analyze([Table("a", [Col("id", "bigint"), Col("addr1"), Col("addr2"), Col("addr3")])]);

        var twoFinding = OfRule(two, DenormalizationRuleIds.RepeatingGroup).Single();
        var threeFinding = OfRule(three, DenormalizationRuleIds.RepeatingGroup).Single();

        threeFinding.Confidence.Should().BeGreaterThan(twoFinding.Confidence);
        threeFinding.Severity.Should().Be(Severity.Medium);
        twoFinding.Severity.Should().Be(Severity.Low);
    }

    [Fact]
    public void RepeatingGroup_IgnoresSingleNumberedColumn()
    {
        var result = Analyze([Table("payments", [Col("id", "bigint"), Col("card_last4")])]);

        OfRule(result, DenormalizationRuleIds.RepeatingGroup).Should().BeEmpty();
    }

    [Fact]
    public void RepeatingGroup_RequiresMatchingDataTypes()
    {
        var result = Analyze([
            Table("t", [Col("id", "bigint"), Col("value1", "text"), Col("value2", "integer")]),
        ]);

        OfRule(result, DenormalizationRuleIds.RepeatingGroup).Should().BeEmpty();
    }

    // ── denorm-derived-column ──────────────────────────────────────────────────

    [Fact]
    public void DerivedColumn_DetectsCachedAggregateWhenChildrenExist()
    {
        var result = Analyze([
            Table("orders",
                [Col("id", "bigint"), Col("item_count", "integer")],
                incoming: [Fk("public", "order_items", "order_id", "public", "orders", "id")]),
        ]);

        var finding = OfRule(result, DenormalizationRuleIds.DerivedColumn).Should().ContainSingle().Subject;
        finding.Columns.Should().BeEquivalentTo(["item_count"]);
        finding.SqlFix!.Sql.Should().Contain("order_items");
    }

    [Fact]
    public void DerivedColumn_SkipsAggregateWhenNoChildTablesReferenceIt()
    {
        var result = Analyze([
            Table("orders", [Col("id", "bigint"), Col("item_count", "integer")]),
        ]);

        OfRule(result, DenormalizationRuleIds.DerivedColumn).Should().BeEmpty();
    }

    [Fact]
    public void DerivedColumn_DetectsConcatenationWithoutRequiringChildren()
    {
        var result = Analyze([
            Table("users", [Col("id", "bigint"), Col("first_name"), Col("last_name"), Col("full_name")]),
        ]);

        var finding = OfRule(result, DenormalizationRuleIds.DerivedColumn).Should().ContainSingle().Subject;
        finding.Columns.Should().BeEquivalentTo(["full_name"]);
    }

    [Fact]
    public void DerivedColumn_IgnoresNonNumericAggregateNames()
    {
        var result = Analyze([
            Table("t",
                [Col("id", "bigint"), Col("order_total", "text")],
                incoming: [Fk("public", "c", "t_id", "public", "t", "id")]),
        ]);

        OfRule(result, DenormalizationRuleIds.DerivedColumn).Should().BeEmpty();
    }

    // ── denorm-duplicated-column ───────────────────────────────────────────────

    [Fact]
    public void DuplicatedColumn_DetectsCopiedColumnBetweenFkLinkedTables()
    {
        var fk = Fk("public", "profiles", "user_id", "public", "users", "id");

        var result = Analyze([
            Table("users", [Col("id", "bigint"), Col("fiscal_code")], incoming: [fk]),
            Table("profiles", [Col("id", "bigint"), Col("user_id", "bigint"), Col("fiscal_code")],
                outgoing: [fk]),
        ]);

        var finding = OfRule(result, DenormalizationRuleIds.DuplicatedColumn).Should().ContainSingle().Subject;
        finding.Columns.Should().BeEquivalentTo(["fiscal_code"]);
        finding.Table.Should().Be("public.profiles");
        finding.SqlFix!.Sql.Should().NotBeNull();
    }

    [Fact]
    public void DuplicatedColumn_IgnoresGenericNames()
    {
        var fk = Fk("public", "b", "a_id", "public", "a", "id");

        var result = Analyze([
            Table("a", [Col("id", "bigint"), Col("name"), Col("created_at", "timestamp")], incoming: [fk]),
            Table("b", [Col("id", "bigint"), Col("a_id", "bigint"), Col("name"), Col("created_at", "timestamp")],
                outgoing: [fk]),
        ]);

        OfRule(result, DenormalizationRuleIds.DuplicatedColumn).Should().BeEmpty();
    }

    [Fact]
    public void DuplicatedColumn_IgnoresForeignKeyColumnsThemselves()
    {
        var fk = Fk("public", "b", "tenant_id", "public", "a", "tenant_id");

        var result = Analyze([
            Table("a", [Col("id", "bigint"), Col("tenant_id", "bigint")], incoming: [fk]),
            Table("b", [Col("id", "bigint"), Col("tenant_id", "bigint")], outgoing: [fk]),
        ]);

        OfRule(result, DenormalizationRuleIds.DuplicatedColumn).Should().BeEmpty();
    }

    [Fact]
    public void DuplicatedColumn_SuppressedWhenTablesAreNotFkLinked()
    {
        var result = Analyze([
            Table("a", [Col("id", "bigint"), Col("fiscal_code")]),
            Table("b", [Col("id", "bigint"), Col("fiscal_code")]),
        ]);

        OfRule(result, DenormalizationRuleIds.DuplicatedColumn).Should().BeEmpty();
    }

    // ── denorm-multivalue-column ───────────────────────────────────────────────

    [Fact]
    public void MultiValueColumn_DetectsCollectionNamedTextColumn()
    {
        var result = Analyze([Table("posts", [Col("id", "bigint"), Col("tags")])]);

        var finding = OfRule(result, DenormalizationRuleIds.MultiValueColumn).Should().ContainSingle().Subject;
        finding.Columns.Should().BeEquivalentTo(["tags"]);
    }

    [Fact]
    public void MultiValueColumn_IgnoresNonTextTypes()
    {
        var result = Analyze([Table("posts", [Col("id", "bigint"), Col("tags", "jsonb")])]);

        OfRule(result, DenormalizationRuleIds.MultiValueColumn).Should().BeEmpty();
    }

    [Fact]
    public void MultiValueColumn_DetectsListSuffix()
    {
        var result = Analyze([Table("t", [Col("id", "bigint"), Col("role_list")])]);

        OfRule(result, DenormalizationRuleIds.MultiValueColumn).Should().ContainSingle();
    }

    // ── denorm-wide-table ──────────────────────────────────────────────────────

    [Fact]
    public void WideTable_FiresOnlyAboveThreshold()
    {
        var columns = Enumerable.Range(0, 12).Select(i => Col($"c{i}_x")).ToList();

        var below = Analyze([Table("t", columns)], new AnalyzeDenormalizationRequest(WideTableColumnThreshold: 12));
        var above = Analyze([Table("t", columns)], new AnalyzeDenormalizationRequest(WideTableColumnThreshold: 11));

        OfRule(below, DenormalizationRuleIds.WideTable).Should().BeEmpty();
        OfRule(above, DenormalizationRuleIds.WideTable).Should().ContainSingle();
    }

    [Fact]
    public void WideTable_MostlyNullableRaisesSeverity()
    {
        var nullable = Enumerable.Range(0, 10).Select(i => Col($"c{i}_x", nullable: true)).ToList();
        var required = Enumerable.Range(0, 10).Select(i => Col($"c{i}_x", nullable: false)).ToList();
        var request = new AnalyzeDenormalizationRequest(WideTableColumnThreshold: 5);

        var nullableFinding = OfRule(Analyze([Table("t", nullable)], request), DenormalizationRuleIds.WideTable).Single();
        var requiredFinding = OfRule(Analyze([Table("t", required)], request), DenormalizationRuleIds.WideTable).Single();

        nullableFinding.Severity.Should().Be(Severity.Low);
        requiredFinding.Severity.Should().Be(Severity.Info);
    }

    // ── Clean schema acceptance ────────────────────────────────────────────────

    [Fact]
    public void CleanSchema_ProducesZeroFindings()
    {
        // Mirrors the demo_good schema, which is the acceptance baseline for every rule.
        var deptFk = Fk("demo_good", "employees", "department_id", "demo_good", "departments", "id");
        var empFk = Fk("demo_good", "projects", "owner_id", "demo_good", "employees", "id");

        var tables = new[]
        {
            Table("departments", [Col("id", "bigint", identity: true), Col("name", "text", nullable: false)],
                schema: "demo_good", incoming: [deptFk]),
            Table("employees",
                [
                    Col("id", "bigint", identity: true),
                    Col("department_id", "bigint", nullable: false),
                    Col("name", "text", nullable: false),
                    Col("email", "text", nullable: false),
                ],
                schema: "demo_good", outgoing: [deptFk], incoming: [empFk]),
            Table("projects",
                [
                    Col("id", "bigint", identity: true),
                    Col("owner_id", "bigint", nullable: false),
                    Col("name", "text", nullable: false),
                    Col("started_at", "timestamp with time zone", nullable: false),
                ],
                schema: "demo_good", outgoing: [empFk]),
        };

        var result = Analyze(tables);

        result.Findings.Should().BeEmpty();
        result.Signals.Should().BeEmpty();
        result.Summary.TotalFindings.Should().Be(0);
        result.Summary.TotalSignals.Should().Be(0);
    }

    // ── Truncation ─────────────────────────────────────────────────────────────

    [Fact]
    public void Truncation_CapsFindingsAndAddsCaveat()
    {
        // Each table contributes one repeating-group finding.
        var tables = Enumerable.Range(0, DiagnosticLimits.MaxFindingCount + 25)
            .Select(i => Table($"t{i}_x", [Col("id", "bigint"), Col("phone1"), Col("phone2")]))
            .ToList();

        var result = Analyze(tables);

        result.Truncated.Should().BeTrue();
        result.Signals.Should().HaveCount(DiagnosticLimits.MaxFindingCount);
        result.Caveats.Should().Contain(c => c.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }
}
