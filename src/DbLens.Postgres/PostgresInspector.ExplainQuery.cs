using System.Text.Json;
using DbLens.Core.Analysis;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Models;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    protected override bool PlansWithoutParameterValues => true;

    protected override async Task<QueryPlanFetch> FetchQueryPlanAsync(
        ExplainQueryRequest request, CancellationToken ct)
    {
        var (sql, arguments) = SqlText.PositionalParameters(request.Sql);
        if (arguments == 0) return new(ParsePlan(await FetchPlanJsonAsync(request, ct)), []);

        var named = SqlText.ParameterNames(request.Sql, DatabaseDialect.PostgreSql).Where(p => p[0] == '@').ToList();
        return new(ParsePlan(await GenericPlanJsonAsync(sql, arguments, ct)),
        [
            "Generic plan: the statement has parameters, so its plan was chosen without their values, as PostgreSQL does for a prepared " +
            "statement it plans generically. The plan a given value gets can differ." +
            (named.Count > 0 ? $" Named parameters were read as positional, in order: {string.Join(", ", named.Select((n, i) => $"{n} = ${i + 1}"))}." : ""),
        ]);
    }

    // The text with parameters is prepared on the server and explained under force_generic_plan (PostgreSQL 12+): the
    // plan for any value, chosen without one. Npgsql would bind any $n as a value, and a plan for "x = NULL" is no
    // plan of the query. EXPLAIN without ANALYZE runs nothing; the statement is deallocated before the connection
    // goes back (a prepared statement outlives the rollback, so only this one is freed, not the driver's own).
    private async Task<string> GenericPlanJsonAsync(string sql, int arguments, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        try
        {
            return await InRolledBackTransactionAsync(conn, ReadOnlyQuerySetupSql, ct, async tx =>
            {
                await using (var setup = Command(conn, "SET LOCAL plan_cache_mode = force_generic_plan", tx))
                    await setup.ExecuteNonQueryAsync(ct);
                await using (var prepare = Command(conn, $"PREPARE dblens_generic_plan AS {sql}", tx))
                    await prepare.ExecuteNonQueryAsync(ct);

                var execute = arguments == 0 ? "" : $"({string.Join(", ", Enumerable.Repeat("NULL", arguments))})";
                await using var explain = Command(conn, $"EXPLAIN (COSTS, VERBOSE, FORMAT JSON) EXECUTE dblens_generic_plan{execute}", tx);
                return (await explain.ExecuteScalarAsync(ct))?.ToString() ?? "[]";
            });
        }
        finally
        {
            // Whether PREPARE ran is asked of the server: a cancellation can land after it ran and before this code knew.
            // A cleanup that fails (a broken connection) must not replace the error that is being thrown.
            try
            {
                await using var exists = Command(conn, "SELECT count(*) FROM pg_prepared_statements WHERE name = 'dblens_generic_plan'");
                if (Convert.ToInt64(await exists.ExecuteScalarAsync(CancellationToken.None), System.Globalization.CultureInfo.InvariantCulture) > 0)
                {
                    await using var deallocate = Command(conn, "DEALLOCATE dblens_generic_plan");
                    await deallocate.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
            catch (System.Data.Common.DbException)
            {
            }
        }
    }

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
        var kind = MapKind(nodeType);

        return new QueryPlanNode(
            NodeType: nodeType,
            Kind: kind,
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
                : null,
            Conversions: kind == QueryPlanNodeKind.TableScan
                ? CastColumns(GetString(e, "Filter"), GetString(e, "Alias"),
                    GetString(e, "Schema") is { } schema ? $"{schema}.{GetString(e, "Relation Name")}" : null)
                : null);
    }

    private static readonly HashSet<string> TypeWords =
        new(["with", "without", "time", "zone", "precision", "varying"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Columns of the scanned relation a filter casts, "(total)::numeric" or "(t.created_at)::timestamp
    /// with time zone". A cast of another relation's column (a correlated or join value, qualified by
    /// its alias) is not this scan's. A cast to text is left out: PostgreSQL casts only varchar and char
    /// to text implicitly (a uuid or integer compared with text is an error), which costs nothing and
    /// keeps an index on the column usable.
    /// </summary>
    private static List<PlanConversion>? CastColumns(string? filter, string? alias, string? table)
    {
        if (filter is null) return null;

        var t = SqlText.Tokens(filter, DatabaseDialect.PostgreSql).Where(k => k.Kind != SqlTokenKind.Space).ToList();
        List<PlanConversion> found = [];
        for (var i = 1; i + 3 < t.Count; i++)
        {
            if (t[i].Source != ")" || t[i + 1].Source != ":" || t[i + 2].Source != ":" || t[i + 3].Kind != SqlTokenKind.Word) continue;

            var j = i - 1;
            if (!IsName(t[j])) continue;
            while (j >= 2 && t[j - 1].Source == "." && IsName(t[j - 2])) j -= 2;
            if (j < 1 || t[j - 1].Source != "(" || (j >= 2 && IsName(t[j - 2]))) continue;
            if (j < i - 1 && t[i - 3].Text != alias) continue;

            var type = t[i + 3].Text;
            var k = i + 4;
            for (; k < t.Count && t[k].Kind == SqlTokenKind.Word && TypeWords.Contains(t[k].Text); k++)
                type += " " + t[k].Text;
            if (k + 1 < t.Count && t[k].Source == "[" && t[k + 1].Source == "]") type += "[]";
            type = type.ToLowerInvariant();
            if (type != "text") found.Add(new PlanConversion(table, t[i - 1].Text, type));
        }

        return found.Count == 0 ? null : found;

        static bool IsName(SqlToken token) => token.Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier;
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
