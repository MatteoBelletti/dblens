using System.Text.Json;
using DbLens.Core.Analysis.QueryPlan;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    protected override async Task<QueryPlanFetch> FetchQueryPlanAsync(
        ExplainQueryRequest request, CancellationToken ct) =>
        new(ParsePlan(await FetchPlanJsonAsync(request, ct)), []);

    // A READ ONLY transaction makes the restriction engine-enforced rather than merely checked,
    // for estimated plans too: a write is still planned without ANALYZE, but nothing can run it.
    private async Task<string> FetchPlanJsonAsync(ExplainQueryRequest request, CancellationToken ct)
    {
        var options = request.Analyze
            ? "ANALYZE, BUFFERS, COSTS, VERBOSE, FORMAT JSON"
            : "COSTS, VERBOSE, FORMAT JSON";

        await using var conn = await OpenAsync(ct);

        return await InRolledBackTransactionAsync(conn, ReadOnlyQuerySetupSql, ct, async tx =>
        {
            await using var cmd = Command(conn, $"EXPLAIN ({options}) {request.Sql}", tx);
            return (await cmd.ExecuteScalarAsync(ct))?.ToString() ?? "[]";
        });
    }

    /// <summary>Maps PostgreSQL's JSON plan onto the dialect-neutral node tree.</summary>
    private static QueryPlanNode? ParsePlan(string json)
    {
        using var doc = JsonDocument.Parse(json);

        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            if (root.GetArrayLength() == 0) return null;
            root = root[0];
        }

        return root.TryGetProperty("Plan", out var plan) ? ParseNode(plan) : null;
    }

    private static QueryPlanNode ParseNode(JsonElement e)
    {
        var nodeType = GetString(e, "Node Type") ?? "Unknown";

        List<QueryPlanNode> children = [];
        if (e.TryGetProperty("Plans", out var plans) && plans.ValueKind == JsonValueKind.Array)
            foreach (var child in plans.EnumerateArray())
                children.Add(ParseNode(child));

        var loops = GetDouble(e, "Actual Loops");

        return new QueryPlanNode(
            NodeType: nodeType,
            Kind: MapKind(nodeType),
            Relation: GetString(e, "Relation Name"),
            IndexName: GetString(e, "Index Name"),
            EstimatedRows: GetDouble(e, "Plan Rows") ?? 0,
            EstimatedCost: GetDouble(e, "Total Cost") ?? 0,
            ActualRows: GetDouble(e, "Actual Rows"),
            ActualTimeMs: GetDouble(e, "Actual Total Time"),
            Loops: loops is null ? null : (long)loops,
            SpilledToDisk: DetectSpill(e),
            Filter: GetString(e, "Filter") ?? GetString(e, "Index Cond") ?? GetString(e, "Recheck Cond"),
            Children: children,
            // Both figures are per-loop averages.
            RowsRead: GetDouble(e, "Actual Rows") is { } returned && GetDouble(e, "Rows Removed by Filter") is { } removed
                ? (returned + removed) * (loops ?? 1)
                : null);
    }

    private static QueryPlanNodeKind MapKind(string nodeType) => nodeType switch
    {
        "Seq Scan" or "Parallel Seq Scan" => QueryPlanNodeKind.TableScan,
        "Index Scan" or "Index Only Scan" or "Parallel Index Scan" or "Bitmap Heap Scan"
            => QueryPlanNodeKind.IndexScan,
        "Bitmap Index Scan" => QueryPlanNodeKind.IndexSeek,
        "Nested Loop" => QueryPlanNodeKind.NestedLoop,
        "Hash Join" => QueryPlanNodeKind.HashJoin,
        "Merge Join" => QueryPlanNodeKind.MergeJoin,
        "Sort" or "Incremental Sort" => QueryPlanNodeKind.Sort,
        "Aggregate" or "HashAggregate" or "GroupAggregate" => QueryPlanNodeKind.Aggregate,
        _ => QueryPlanNodeKind.Other,
    };

    /// <summary>
    /// A sort reports "Disk" as its space type when it could not finish in work_mem;
    /// a hash reports more than one batch when it had to spill.
    /// </summary>
    private static bool DetectSpill(JsonElement e)
    {
        if (string.Equals(GetString(e, "Sort Space Type"), "Disk", StringComparison.OrdinalIgnoreCase))
            return true;

        if (GetString(e, "Sort Method")?.Contains("external", StringComparison.OrdinalIgnoreCase) == true)
            return true;

        return GetDouble(e, "Hash Batches") > 1;
    }

    private static string? GetString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
