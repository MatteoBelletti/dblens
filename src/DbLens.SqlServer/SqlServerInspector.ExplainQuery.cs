using System.Globalization;
using System.Xml.Linq;
using DbLens.Core.Analysis.QueryPlan;
using Microsoft.Data.SqlClient;

namespace DbLens.SqlServer;

public sealed partial class SqlServerInspector
{
    private static readonly XNamespace ShowPlanNs =
        "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    protected override async Task<QueryPlanFetch> FetchQueryPlanAsync(
        ExplainQueryRequest request, CancellationToken ct)
    {
        if (!request.Analyze)
            return new(ParsePlan(await FetchEstimatedPlanAsync(request, ct)), []);

        return new(
            ParsePlan(await FetchActualPlanAsync(request, ct)),
            ["SQL Server has no read-only transaction mode, so the second guard here is a " +
             "transaction that is always rolled back rather than an engine-enforced " +
             "restriction. The statement allow-list is what prevents writes from being sent."]);
    }

    /// <summary>SHOWPLAN_XML returns the plan without running the statement.</summary>
    private async Task<string> FetchEstimatedPlanAsync(ExplainQueryRequest request, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await ExecuteAsync(conn, null, "SET SHOWPLAN_XML ON", ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = request.Sql;
            cmd.CommandTimeout = QueryTimeoutSeconds;
            return await ReadFirstStringAsync(cmd, ct);
        }
        finally
        {
            await ExecuteAsync(conn, null, "SET SHOWPLAN_XML OFF", ct);
        }
    }

    /// <summary>
    /// STATISTICS XML runs the statement and returns the plan with actual row counts appended
    /// as a trailing result set. The transaction is always rolled back.
    /// </summary>
    private async Task<string> FetchActualPlanAsync(ExplainQueryRequest request, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await ExecuteAsync(conn, tx, "SET STATISTICS XML ON", ct);

            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = request.Sql;
            cmd.CommandTimeout = QueryTimeoutSeconds;

            return await ReadPlanFromResultSetsAsync(cmd, ct);
        }
        finally
        {
            await tx.RollbackAsync(ct);
        }
    }

    private static async Task ExecuteAsync(
        SqlConnection conn, SqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> ReadFirstStringAsync(SqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct) && !reader.IsDBNull(0))
            return reader.GetString(0);
        return "";
    }

    /// <summary>
    /// Walks every result set and returns the last one that looks like a showplan document,
    /// which is where STATISTICS XML puts the plan carrying actual row counts.
    /// </summary>
    private static async Task<string> ReadPlanFromResultSetsAsync(SqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var plan = "";

        do
        {
            while (await reader.ReadAsync(ct))
            {
                if (reader.FieldCount != 1 || reader.IsDBNull(0)) continue;
                if (reader.GetFieldType(0) != typeof(string)) continue;

                var value = reader.GetString(0);
                if (value.Contains("ShowPlanXML", StringComparison.Ordinal))
                    plan = value;
            }
        } while (await reader.NextResultAsync(ct));

        return plan;
    }

    private static QueryPlanNode? ParsePlan(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var relOp = doc.Descendants(ShowPlanNs + "RelOp").FirstOrDefault();
        return relOp is null ? null : ParseRelOp(relOp);
    }

    private static QueryPlanNode ParseRelOp(XElement relOp)
    {
        var physicalOp = (string?)relOp.Attribute("PhysicalOp") ?? "Unknown";

        // Nested RelOps are the children; they can sit at any depth under the operator's
        // type-specific wrapper element (IndexScan, Hash, NestedLoops, ...).
        var children = relOp.Elements()
            .DescendantsAndSelf(ShowPlanNs + "RelOp")
            .Where(e => e.Ancestors(ShowPlanNs + "RelOp").FirstOrDefault() == relOp)
            .Select(ParseRelOp)
            .ToList();

        var runtime = relOp.Descendants(ShowPlanNs + "RunTimeCountersPerThread")
            .Where(e => e.Ancestors(ShowPlanNs + "RelOp").FirstOrDefault() == relOp)
            .ToList();

        double? actualRows = runtime.Count > 0
            ? runtime.Sum(e => ParseDouble((string?)e.Attribute("ActualRows")) ?? 0)
            : null;

        double? actualTimeMs = runtime.Count > 0
            ? runtime.Max(e => ParseDouble((string?)e.Attribute("ActualElapsedms")) ?? 0)
            : null;

        long? executions = runtime.Count > 0
            ? (long)runtime.Sum(e => ParseDouble((string?)e.Attribute("ActualExecutions")) ?? 0)
            : null;

        var objectElement = relOp.Descendants(ShowPlanNs + "Object")
            .FirstOrDefault(e => e.Ancestors(ShowPlanNs + "RelOp").FirstOrDefault() == relOp);

        return new QueryPlanNode(
            NodeType: physicalOp,
            Kind: MapKind(physicalOp),
            Relation: Clean((string?)objectElement?.Attribute("Table")),
            IndexName: Clean((string?)objectElement?.Attribute("Index")),
            EstimatedRows: ParseDouble((string?)relOp.Attribute("EstimateRows")) ?? 0,
            EstimatedCost: ParseDouble((string?)relOp.Attribute("EstimatedTotalSubtreeCost")) ?? 0,
            ActualRows: actualRows,
            ActualTimeMs: actualTimeMs,
            Loops: executions,
            SpilledToDisk: relOp.Descendants(ShowPlanNs + "SpillToTempDb").Any(),
            Filter: Clean((string?)relOp.Descendants(ShowPlanNs + "Predicate")
                .FirstOrDefault()?.Descendants(ShowPlanNs + "ScalarOperator")
                .FirstOrDefault()?.Attribute("ScalarString")),
            Children: children);
    }

    private static QueryPlanNodeKind MapKind(string physicalOp) => physicalOp switch
    {
        "Table Scan" or "Clustered Index Scan" or "Columnstore Index Scan" => QueryPlanNodeKind.TableScan,
        "Index Scan" => QueryPlanNodeKind.IndexScan,
        "Index Seek" or "Clustered Index Seek" or "Key Lookup" or "RID Lookup" => QueryPlanNodeKind.IndexSeek,
        "Nested Loops" => QueryPlanNodeKind.NestedLoop,
        "Hash Match" => QueryPlanNodeKind.HashJoin,
        "Merge Join" => QueryPlanNodeKind.MergeJoin,
        "Sort" or "Top N Sort" => QueryPlanNodeKind.Sort,
        "Stream Aggregate" or "Hash Match Aggregate" => QueryPlanNodeKind.Aggregate,
        _ => QueryPlanNodeKind.Other,
    };

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static string? Clean(string? identifier) =>
        string.IsNullOrWhiteSpace(identifier) ? null : identifier.Trim().Trim('[', ']');
}
