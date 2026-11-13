using System.Text.Json;
using DbLens.Core.Analysis.QueryPlan;
using Npgsql;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    protected override async Task<QueryPlanFetch> FetchQueryPlanAsync(
        ExplainQueryRequest request, CancellationToken ct) =>
        new(ParsePlan(await FetchPlanJsonAsync(request, ct)), []);

    private async Task<string> FetchPlanJsonAsync(ExplainQueryRequest request, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var options = request.Analyze
            ? "ANALYZE, BUFFERS, COSTS, VERBOSE, FORMAT JSON"
            : "COSTS, VERBOSE, FORMAT JSON";

        if (!request.Analyze)
        {
            await using var plain = conn.CreateCommand();
            plain.CommandText = $"EXPLAIN ({options}) {request.Sql}";
            plain.CommandTimeout = QueryTimeoutSeconds;
            return (await plain.ExecuteScalarAsync(ct))?.ToString() ?? "[]";
        }

        // Second guard: a READ ONLY transaction makes the restriction engine-enforced rather
        // than merely checked, and the rollback discards anything the statement touched.
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var readOnly = conn.CreateCommand())
            {
                readOnly.Transaction = tx;
                readOnly.CommandText = "SET TRANSACTION READ ONLY";
                readOnly.CommandTimeout = QueryTimeoutSeconds;
                await readOnly.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"EXPLAIN ({options}) {request.Sql}";
            cmd.CommandTimeout = QueryTimeoutSeconds;

            var result = (await cmd.ExecuteScalarAsync(ct))?.ToString() ?? "[]";
            return result;
        }
        finally
        {
            await tx.RollbackAsync(ct);
        }
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

        var children = new List<QueryPlanNode>();
        if (e.TryGetProperty("Plans", out var plans) && plans.ValueKind == JsonValueKind.Array)
            foreach (var child in plans.EnumerateArray())
                children.Add(ParseNode(child));

        return new QueryPlanNode(
            NodeType: nodeType,
            Kind: MapKind(nodeType),
            Relation: GetString(e, "Relation Name"),
            IndexName: GetString(e, "Index Name"),
            EstimatedRows: GetDouble(e, "Plan Rows") ?? 0,
            EstimatedCost: GetDouble(e, "Total Cost") ?? 0,
            ActualRows: GetDouble(e, "Actual Rows"),
            ActualTimeMs: GetDouble(e, "Actual Total Time"),
            Loops: GetDouble(e, "Actual Loops") is { } loops ? (long)loops : null,
            SpilledToDisk: DetectSpill(e),
            Filter: GetString(e, "Filter") ?? GetString(e, "Index Cond") ?? GetString(e, "Recheck Cond"),
            Children: children);
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
