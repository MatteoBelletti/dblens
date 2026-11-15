using DbLens.Core.Models;

namespace DbLens.Core.Analysis.QueryPlan;

/// <summary>
/// Pure in-memory execution plan analysis. No database access — operates on a normalized plan
/// tree supplied by the caller (dialect-specific inspector).
/// </summary>
public static class QueryPlanAnalysisEngine
{
    public const string Disclaimer =
        "A plan describes how the engine intends to run one query against the data and statistics " +
        "present right now; the same query can get a different plan tomorrow. " +
        "Without Analyze, row counts are optimizer predictions, not measurements — a prediction that " +
        "is badly wrong is itself the most common cause of a bad plan. " +
        "These rules flag shapes that are frequently slow; they do not prove a query is slow, " +
        "and a full scan is often the correct choice on a small table.";

    public static ExplainQueryResult Analyze(
        QueryPlanNode? root,
        string planText,
        bool executed,
        ExplainQueryRequest request,
        DatabaseDialect dialect)
    {
        List<string> caveats = [];
        List<Finding> findings = [];

        if (root is null)
        {
            caveats.Add(
                "The plan could not be parsed into structured nodes; only the raw plan text is available. " +
                "No rules were evaluated.");

            return Build(FindingSplit.Empty, executed, planText, null, caveats, 0, null, null);
        }

        var nodes = root.DescendantsAndSelf().ToList();

        foreach (var node in nodes)
        {
            if (IsLargeTableScan(node, request))
                findings.Add(BuildSeqScanFinding(node, request, dialect));

            if (executed && HasEstimateMismatch(node, request))
                findings.Add(BuildEstimateMismatchFinding(node, request));

            if (IsExpensiveNestedLoop(node, request))
                findings.Add(BuildNestedLoopFinding(node, request));

            if (node.SpilledToDisk)
                findings.Add(BuildSpillFinding(node, dialect));

            if (HasFilterOnScan(node))
                findings.Add(BuildScanWithFilterFinding(node, dialect));
        }

        if (!executed)
            caveats.Add(
                "The query was not executed, so every row count in this plan is an optimizer estimate. " +
                "Re-run with analyze = true to compare estimates against reality — the gap between them " +
                "is usually more informative than the plan shape alone.");

        var split = FindingSplit.Partition(findings);

        if (split.TruncationCaveat(hint: null) is { } truncation)
            caveats.Add(truncation);

        return Build(split, executed, planText, root, caveats,
            nodes.Count, root.EstimatedCost, root.ActualTimeMs);
    }

    private static ExplainQueryResult Build(
        FindingSplit split,
        bool executed,
        string planText,
        QueryPlanNode? root,
        IReadOnlyList<string> caveats,
        int nodeCount,
        double? totalCost,
        double? totalTimeMs) =>
        new(
            Executed: executed,
            PlanText: planText,
            Root: root,
            Findings: split.Findings,
            Signals: split.Signals,
            Summary: BuildSummary(split, nodeCount, totalCost, totalTimeMs),
            Caveats: caveats,
            Truncated: split.Truncated,
            Disclaimer: Disclaimer);

    /// <summary>
    /// Renders the normalized tree as indented text. Both engines emit verbose native plan
    /// formats (JSON on PostgreSQL, XML on SQL Server) that are large and dialect-specific;
    /// this gives one compact rendering the caller can read directly.
    /// </summary>
    public static string RenderPlanText(QueryPlanNode? root)
    {
        if (root is null) return "(no plan)";

        var sb = new System.Text.StringBuilder();
        Render(root, 0);
        return sb.ToString().TrimEnd();

        void Render(QueryPlanNode node, int depth)
        {
            sb.Append(new string(' ', depth * 2)).Append("-> ").Append(node.NodeType);

            if (node.Relation is not null) sb.Append(" on ").Append(node.Relation);
            if (node.IndexName is not null) sb.Append(" using ").Append(node.IndexName);

            sb.Append("  (est rows=").Append(Num.N0(node.EstimatedRows))
              .Append(" cost=").Append(Num.F1(node.EstimatedCost));

            if (node.ActualRows is { } actual)
            {
                sb.Append(" | actual rows=").Append(Num.N0(actual));
                if (node.Loops is { } loops && loops > 1) sb.Append(" loops=").Append(Num.N0(loops));
                if (node.ActualTimeMs is { } ms) sb.Append(" time=").Append(Num.F1(ms)).Append("ms");
            }

            sb.Append(')');

            if (node.SpilledToDisk) sb.Append(" [spilled to disk]");
            if (node.Filter is not null) sb.Append("\n").Append(new string(' ', depth * 2 + 5))
                                            .Append("Filter: ").Append(node.Filter);

            sb.Append('\n');

            foreach (var child in node.Children)
                Render(child, depth + 1);
        }
    }

    // ── Rule predicates ────────────────────────────────────────────────────────

    private static bool IsLargeTableScan(QueryPlanNode node, ExplainQueryRequest request)
    {
        if (node.Kind != QueryPlanNodeKind.TableScan) return false;

        return ScanRows(node) >= request.LargeScanRowThreshold;
    }

    // What the scan cost is the rows it read; what it returned after filtering is the fallback,
    // and the estimate the last resort.
    private static double ScanRows(QueryPlanNode node) =>
        node.RowsRead ?? node.TotalActualRows ?? node.EstimatedRows;

    private static bool HasEstimateMismatch(QueryPlanNode node, ExplainQueryRequest request)
    {
        if (node.ActualRows is not { } actual) return false;

        var estimated = node.EstimatedRows;

        // Small absolute numbers produce meaningless ratios (1 vs 20 is a factor of 20).
        if (actual < 100 && estimated < 100) return false;

        var ratio = estimated <= 0
            ? actual
            : Math.Max(actual / estimated, estimated / Math.Max(actual, 1));

        return ratio >= request.EstimateMismatchFactor;
    }

    private static bool IsExpensiveNestedLoop(QueryPlanNode node, ExplainQueryRequest request)
    {
        if (node.Kind != QueryPlanNodeKind.NestedLoop) return false;
        if (node.Children.Count == 0) return false;

        // A nested loop re-executes its inner side once per outer row; the cost is driven by
        // how many times that happens, not by the loop node's own row count.
        var outer = node.Children[0];
        var outerRows = outer.TotalActualRows ?? outer.EstimatedRows;

        return outerRows >= request.NestedLoopRowThreshold;
    }

    private static bool HasFilterOnScan(QueryPlanNode node) =>
        node.Kind == QueryPlanNodeKind.TableScan
        && !string.IsNullOrWhiteSpace(node.Filter)
        && node.Relation is not null;

    // ── Finding builders ───────────────────────────────────────────────────────

    private static Finding BuildSeqScanFinding(
        QueryPlanNode node, ExplainQueryRequest request, DatabaseDialect dialect)
    {
        var rows = ScanRows(node);
        var measured = node.TotalActualRows is not null;
        var verb = node.RowsRead is null ? "producing" : "reading";
        var relation = node.Relation ?? "(unknown)";

        return new Finding(
            RuleId:     QueryPlanRuleIds.SeqScanLargeTable,
            Severity:   rows >= request.LargeScanRowThreshold * 10 ? Severity.High : Severity.Medium,
            Category:   FindingCategory.Performance,
            Confidence: measured ? 0.9 : 0.6,
            Evidence:   measured ? Evidence.Measured : Evidence.Derived,
            Table:      relation,
            Columns:    null,
            Title:      $"{node.NodeType} over {Num.N0(rows)} rows on '{relation}'",
            Description:
                $"The plan reads '{relation}' with a {node.NodeType}, {verb} {Num.N0(rows)} rows " +
                $"({(measured ? "measured" : "estimated")}), at or above the threshold of " +
                $"{Num.N0(request.LargeScanRowThreshold)}. " +
                "A full scan on a large table is often a missing or unusable index — for example a " +
                "predicate wrapped in a function, a type mismatch that prevents an index match, or a " +
                "leading wildcard in a LIKE. " +
                "It is also the correct plan when the query genuinely needs most of the table.",
            SqlFix:     null,
            Metadata:   new Dictionary<string, object>
            {
                ["nodeType"] = node.NodeType,
                ["rows"] = Math.Round(rows, 0),
                ["measured"] = measured,
                ["estimatedCost"] = Math.Round(node.EstimatedCost, 2),
                ["filter"] = node.Filter ?? "",
            });
    }

    private static Finding BuildEstimateMismatchFinding(QueryPlanNode node, ExplainQueryRequest request)
    {
        var actual = node.ActualRows!.Value;
        var estimated = node.EstimatedRows;
        var overestimate = estimated > actual;

        return new Finding(
            RuleId:     QueryPlanRuleIds.EstimateMismatch,
            Severity:   Severity.High,
            Category:   FindingCategory.Performance,
            Confidence: 1.0,
            Evidence:   Evidence.Measured,
            Table:      node.Relation ?? node.NodeType,
            Columns:    null,
            Title:
                $"{node.NodeType} estimated {Num.N0(estimated)} rows but produced {Num.N0(actual)}",
            Description:
                $"The optimizer {(overestimate ? "over" : "under")}estimated this node by a factor of " +
                $"{Num.F1(estimated <= 0 ? actual : Math.Max(actual / estimated, estimated / Math.Max(actual, 1)))}. " +
                "The planner chooses join order, join algorithms and access methods from these estimates, " +
                "so a large error here often explains a bad plan elsewhere in the query. " +
                "Common causes: statistics not refreshed after a bulk load, correlated predicates the " +
                "planner assumes are independent, or a skewed value distribution. " +
                "Refresh statistics (ANALYZE on PostgreSQL, UPDATE STATISTICS on SQL Server) and re-check.",
            SqlFix:     null,
            Metadata:   new Dictionary<string, object>
            {
                ["nodeType"] = node.NodeType,
                ["estimatedRows"] = Math.Round(estimated, 0),
                ["actualRows"] = Math.Round(actual, 0),
                ["direction"] = overestimate ? "overestimate" : "underestimate",
            });
    }

    private static Finding BuildNestedLoopFinding(QueryPlanNode node, ExplainQueryRequest request)
    {
        var outer = node.Children[0];
        var outerRows = outer.TotalActualRows ?? outer.EstimatedRows;
        var measured = outer.TotalActualRows is not null;

        return new Finding(
            RuleId:     QueryPlanRuleIds.NestedLoopLargeInput,
            Severity:   Severity.Medium,
            Category:   FindingCategory.Performance,
            Confidence: measured ? 0.8 : 0.5,
            Evidence:   measured ? Evidence.Measured : Evidence.Derived,
            Table:      node.Relation ?? outer.Relation ?? node.NodeType,
            Columns:    null,
            Title:      $"Nested loop driven by {Num.N0(outerRows)} outer rows",
            Description:
                $"This nested loop executes its inner side once per outer row, and the outer side " +
                $"produces {Num.N0(outerRows)} rows ({(measured ? "measured" : "estimated")}). " +
                "Nested loops are efficient when the outer side is small and the inner side is an index " +
                "seek; with a large outer input the repeated lookups dominate the runtime. " +
                "A hash join is usually better at this size. " +
                "If the outer estimate is wrong, fixing the statistics may be enough for the planner to " +
                "choose a different join on its own.",
            SqlFix:     null,
            Metadata:   new Dictionary<string, object>
            {
                ["outerRows"] = Math.Round(outerRows, 0),
                ["measured"] = measured,
                ["threshold"] = request.NestedLoopRowThreshold,
            });
    }

    private static Finding BuildSpillFinding(QueryPlanNode node, DatabaseDialect dialect)
    {
        var knob = dialect == DatabaseDialect.PostgreSql
            ? "work_mem"
            : "the memory grant (see Resource Governor / MAXDOP and query memory settings)";

        return new Finding(
            RuleId:     QueryPlanRuleIds.SortSpilledToDisk,
            Severity:   Severity.Medium,
            Category:   FindingCategory.Performance,
            Confidence: 1.0,
            Evidence:   Evidence.Measured,
            Table:      node.Relation ?? node.NodeType,
            Columns:    null,
            Title:      $"{node.NodeType} spilled to disk",
            Description:
                $"This {node.NodeType} could not complete in memory and wrote to disk, which is far " +
                "slower than an in-memory operation. " +
                $"Either the operator needs more memory ({knob}) or it is processing more rows than " +
                "necessary — reducing the row count before the sort, or supporting it with an index that " +
                "already provides the required order, removes the spill entirely.",
            SqlFix:     null,
            Metadata:   new Dictionary<string, object>
            {
                ["nodeType"] = node.NodeType,
            });
    }

    private static Finding BuildScanWithFilterFinding(QueryPlanNode node, DatabaseDialect dialect)
    {
        var relation = node.Relation!;

        return new Finding(
            RuleId:     QueryPlanRuleIds.ScanWithFilter,
            Severity:   Severity.Low,
            Category:   FindingCategory.Performance,
            Confidence: 0.5,
            Evidence:   Evidence.Derived,
            Table:      relation,
            Columns:    null,
            Title:      $"Filter applied during a full scan of '{relation}'",
            Description:
                $"'{relation}' is read in full and then filtered by: {node.Filter}. " +
                "When a predicate is evaluated after reading every row, an index on the filtered " +
                "columns can often turn the scan into a seek. " +
                "Check whether the predicate is index-usable: a column wrapped in a function, an " +
                "implicit type conversion, or a LIKE with a leading wildcard all prevent index use " +
                "even when a suitable index exists. " +
                "Use analyze_indexes to see which indexes are already present on this table.",
            SqlFix:     null,
            Metadata:   new Dictionary<string, object>
            {
                ["filter"] = node.Filter!,
                ["nodeType"] = node.NodeType,
            });
    }

    private static QueryPlanSummary BuildSummary(
        FindingSplit split,
        int nodeCount,
        double? totalCost,
        double? totalTimeMs) =>
        new(
            TotalFindings: split.Findings.Count,
            TotalSignals: split.Signals.Count,
            NodeCount: nodeCount,
            TotalEstimatedCost: totalCost is null ? null : Math.Round(totalCost.Value, 2),
            TotalActualTimeMs: totalTimeMs is null ? null : Math.Round(totalTimeMs.Value, 2),
            BySeverity: split.BySeverity(),
            ByRule: split.ByRule(QueryPlanRuleIds.All));
}
