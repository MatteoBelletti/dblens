using DbLens.Core.Analysis;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Models;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// Unit tests for QueryPlanAnalysisEngine — pure in-memory logic with no database access.
/// </summary>
public sealed class QueryPlanAnalysisTests
{
    // ── Builders ───────────────────────────────────────────────────────────────

    private static QueryPlanNode Node(
        string nodeType,
        QueryPlanNodeKind kind,
        double estimatedRows = 10,
        double? actualRows = null,
        string? relation = "public.users",
        double cost = 1.0,
        long? loops = null,
        bool spilled = false,
        string? filter = null,
        params QueryPlanNode[] children) =>
        new(nodeType, kind, relation, null, estimatedRows, cost, actualRows,
            actualRows is null ? null : 1.0, loops, spilled, filter, children);

    private static ExplainQueryResult Analyze(
        QueryPlanNode? root,
        bool executed = false,
        ExplainQueryRequest? request = null,
        DatabaseDialect dialect = DatabaseDialect.PostgreSql) =>
        QueryPlanAnalysisEngine.Analyze(
            root,
            "plan text",
            executed,
            request ?? new ExplainQueryRequest("SELECT 1"),
            dialect);

    private static IEnumerable<Finding> OfRule(ExplainQueryResult r, string ruleId) =>
        r.Findings.Where(f => f.RuleId == ruleId);

    // ── Summary contract ───────────────────────────────────────────────────────

    [Fact]
    public void Summary_AlwaysContainsEveryRuleAndSeverityKey()
    {
        var result = Analyze(Node("Result", QueryPlanNodeKind.Other));

        foreach (var rule in QueryPlanRuleIds.All)
            result.Summary.ByRule.Should().ContainKey(rule);

        foreach (var severity in Enum.GetValues<Severity>())
            result.Summary.BySeverity.Should().ContainKey(severity.ToString());
    }

    [Fact]
    public void NullPlan_ProducesCaveatAndNoFindings()
    {
        var result = Analyze(null);

        result.Findings.Should().BeEmpty();
        result.Root.Should().BeNull();
        result.Caveats.Should().Contain(c => c.Contains("could not be parsed"));
    }

    [Fact]
    public void NodeCount_CountsTheWholeTree()
    {
        var root = Node("Nested Loop", QueryPlanNodeKind.NestedLoop, children:
        [
            Node("Seq Scan", QueryPlanNodeKind.TableScan),
            Node("Index Scan", QueryPlanNodeKind.IndexScan),
        ]);

        Analyze(root).Summary.NodeCount.Should().Be(3);
    }

    // ── plan-seq-scan-large-table ──────────────────────────────────────────────

    [Fact]
    public void SeqScan_FiresAboveThreshold()
    {
        var result = Analyze(Node("Seq Scan", QueryPlanNodeKind.TableScan, estimatedRows: 50_000));

        OfRule(result, QueryPlanRuleIds.SeqScanLargeTable).Should().ContainSingle();
    }

    [Fact]
    public void SeqScan_DoesNotFireBelowThreshold()
    {
        var result = Analyze(Node("Seq Scan", QueryPlanNodeKind.TableScan, estimatedRows: 100));

        OfRule(result, QueryPlanRuleIds.SeqScanLargeTable).Should().BeEmpty();
    }

    [Fact]
    public void SeqScan_IndexScanIsNeverReported()
    {
        var result = Analyze(Node("Index Scan", QueryPlanNodeKind.IndexScan, estimatedRows: 5_000_000));

        OfRule(result, QueryPlanRuleIds.SeqScanLargeTable).Should().BeEmpty();
    }

    [Fact]
    public void SeqScan_MeasuredRowsRaiseConfidenceOverEstimates()
    {
        var estimated = Analyze(Node("Seq Scan", QueryPlanNodeKind.TableScan, estimatedRows: 50_000));
        var measured = Analyze(
            Node("Seq Scan", QueryPlanNodeKind.TableScan, estimatedRows: 50_000, actualRows: 50_000, loops: 1),
            executed: true);

        var e = OfRule(estimated, QueryPlanRuleIds.SeqScanLargeTable).Single();
        var m = OfRule(measured, QueryPlanRuleIds.SeqScanLargeTable).Single();

        m.Confidence.Should().BeGreaterThan(e.Confidence);
    }

    [Fact]
    public void SeqScan_UsesTotalRowsAcrossLoops()
    {
        // 1,000 rows per execution across 100 loops is 100,000 rows of work.
        var node = Node("Seq Scan", QueryPlanNodeKind.TableScan,
            estimatedRows: 1_000, actualRows: 1_000, loops: 100);

        OfRule(Analyze(node, executed: true), QueryPlanRuleIds.SeqScanLargeTable).Should().ContainSingle();
    }

    // ── plan-estimate-mismatch ─────────────────────────────────────────────────

    [Fact]
    public void EstimateMismatch_OnlyEvaluatedWhenExecuted()
    {
        var node = Node("Seq Scan", QueryPlanNodeKind.TableScan, estimatedRows: 100, actualRows: 100_000, loops: 1);

        OfRule(Analyze(node, executed: false), QueryPlanRuleIds.EstimateMismatch).Should().BeEmpty();
        OfRule(Analyze(node, executed: true), QueryPlanRuleIds.EstimateMismatch).Should().ContainSingle();
    }

    [Fact]
    public void EstimateMismatch_DetectsBothOverAndUnderEstimates()
    {
        var under = Node("Seq Scan", QueryPlanNodeKind.TableScan, estimatedRows: 100, actualRows: 100_000, loops: 1);
        var over = Node("Seq Scan", QueryPlanNodeKind.TableScan, estimatedRows: 100_000, actualRows: 100, loops: 1);

        OfRule(Analyze(under, executed: true), QueryPlanRuleIds.EstimateMismatch).Single()
            .Metadata!["direction"].Should().Be("underestimate");
        OfRule(Analyze(over, executed: true), QueryPlanRuleIds.EstimateMismatch).Single()
            .Metadata!["direction"].Should().Be("overestimate");
    }

    [Fact]
    public void EstimateMismatch_IgnoresSmallAbsoluteNumbers()
    {
        // 1 estimated vs 20 actual is a factor of 20 but means nothing at this scale.
        var node = Node("Seq Scan", QueryPlanNodeKind.TableScan, estimatedRows: 1, actualRows: 20, loops: 1);

        OfRule(Analyze(node, executed: true), QueryPlanRuleIds.EstimateMismatch).Should().BeEmpty();
    }

    [Fact]
    public void EstimateMismatch_RespectsCustomFactor()
    {
        var node = Node("Seq Scan", QueryPlanNodeKind.TableScan, estimatedRows: 1_000, actualRows: 5_000, loops: 1);
        var strict = new ExplainQueryRequest("SELECT 1", EstimateMismatchFactor: 2);
        var lenient = new ExplainQueryRequest("SELECT 1", EstimateMismatchFactor: 100);

        OfRule(Analyze(node, true, strict), QueryPlanRuleIds.EstimateMismatch).Should().ContainSingle();
        OfRule(Analyze(node, true, lenient), QueryPlanRuleIds.EstimateMismatch).Should().BeEmpty();
    }

    // ── plan-nested-loop-large-input ───────────────────────────────────────────

    [Fact]
    public void NestedLoop_FiresOnLargeOuterInput()
    {
        var root = Node("Nested Loop", QueryPlanNodeKind.NestedLoop, children:
        [
            Node("Seq Scan", QueryPlanNodeKind.TableScan, estimatedRows: 500_000),
            Node("Index Scan", QueryPlanNodeKind.IndexScan, estimatedRows: 1),
        ]);

        OfRule(Analyze(root), QueryPlanRuleIds.NestedLoopLargeInput).Should().ContainSingle();
    }

    [Fact]
    public void NestedLoop_DoesNotFireOnSmallOuterInput()
    {
        var root = Node("Nested Loop", QueryPlanNodeKind.NestedLoop, children:
        [
            Node("Index Scan", QueryPlanNodeKind.IndexScan, estimatedRows: 5),
            Node("Index Scan", QueryPlanNodeKind.IndexScan, estimatedRows: 1),
        ]);

        OfRule(Analyze(root), QueryPlanRuleIds.NestedLoopLargeInput).Should().BeEmpty();
    }

    [Fact]
    public void NestedLoop_WithoutChildrenIsIgnored()
    {
        OfRule(Analyze(Node("Nested Loop", QueryPlanNodeKind.NestedLoop)),
            QueryPlanRuleIds.NestedLoopLargeInput).Should().BeEmpty();
    }

    // ── plan-sort-spilled-to-disk ──────────────────────────────────────────────

    [Fact]
    public void Spill_IsReportedWithDialectSpecificAdvice()
    {
        var node = Node("Sort", QueryPlanNodeKind.Sort, spilled: true);

        OfRule(Analyze(node, dialect: DatabaseDialect.PostgreSql), QueryPlanRuleIds.SortSpilledToDisk)
            .Single().Description.Should().Contain("work_mem");
        OfRule(Analyze(node, dialect: DatabaseDialect.SqlServer), QueryPlanRuleIds.SortSpilledToDisk)
            .Single().Description.Should().Contain("memory grant");
    }

    // ── plan-scan-with-filter ──────────────────────────────────────────────────

    [Fact]
    public void ScanWithFilter_FiresOnFilteredTableScan()
    {
        var node = Node("Seq Scan", QueryPlanNodeKind.TableScan, filter: "(email = 'x')");

        OfRule(Analyze(node), QueryPlanRuleIds.ScanWithFilter).Should().ContainSingle();
    }

    [Fact]
    public void ScanWithFilter_DoesNotFireOnIndexScan()
    {
        var node = Node("Index Scan", QueryPlanNodeKind.IndexScan, filter: "(email = 'x')");

        OfRule(Analyze(node), QueryPlanRuleIds.ScanWithFilter).Should().BeEmpty();
    }

    // ── Estimate caveat ────────────────────────────────────────────────────────

    [Fact]
    public void NotExecuted_AddsCaveatThatRowCountsAreEstimates()
    {
        var result = Analyze(Node("Result", QueryPlanNodeKind.Other), executed: false);

        result.Executed.Should().BeFalse();
        result.Caveats.Should().Contain(c => c.Contains("optimizer estimate"));
    }

    [Fact]
    public void Executed_DoesNotAddTheEstimateCaveat()
    {
        var result = Analyze(Node("Result", QueryPlanNodeKind.Other), executed: true);

        result.Executed.Should().BeTrue();
        result.Caveats.Should().NotContain(c => c.Contains("optimizer estimate"));
    }

    // ── Plan rendering ─────────────────────────────────────────────────────────

    [Fact]
    public void RenderPlanText_ShowsTreeStructureAndMetrics()
    {
        var root = Node("Nested Loop", QueryPlanNodeKind.NestedLoop, estimatedRows: 100, children:
        [
            Node("Seq Scan", QueryPlanNodeKind.TableScan, estimatedRows: 5_000, relation: "public.orders"),
        ]);

        var text = QueryPlanAnalysisEngine.RenderPlanText(root);

        text.Should().Contain("Nested Loop");
        text.Should().Contain("Seq Scan on public.orders");
        text.Should().Contain("5,000");
    }

    [Fact]
    public void RenderPlanText_HandlesNullRoot()
    {
        QueryPlanAnalysisEngine.RenderPlanText(null).Should().Be("(no plan)");
    }

    [Fact]
    public void RenderPlanText_MarksSpills()
    {
        var text = QueryPlanAnalysisEngine.RenderPlanText(
            Node("Sort", QueryPlanNodeKind.Sort, spilled: true));

        text.Should().Contain("spilled to disk");
    }

    // ── Invariant formatting ───────────────────────────────────────────────────

    [Fact]
    public void Findings_FormatNumbersInvariantlyRegardlessOfCulture()
    {
        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("it-IT");

            var result = Analyze(Node("Seq Scan", QueryPlanNodeKind.TableScan, estimatedRows: 1_500_000));

            OfRule(result, QueryPlanRuleIds.SeqScanLargeTable).Single()
                .Title.Should().Contain("1,500,000");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
