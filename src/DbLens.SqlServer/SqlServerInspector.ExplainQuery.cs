using System.Data.Common;
using System.Globalization;
using System.Xml.Linq;
using DbLens.Core.Analysis.QueryPlan;

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
        await using var conn = await OpenAsync(ct);

        await ExecuteAsync(conn, "SET SHOWPLAN_XML ON", null, ct);
        try
        {
            await using var cmd = Command(conn, request.Sql);
            return await cmd.ExecuteScalarAsync(ct) as string ?? "";
        }
        finally
        {
            await ExecuteAsync(conn, "SET SHOWPLAN_XML OFF", null, ct);
        }
    }

    /// <summary>
    /// STATISTICS XML runs the statement and appends the plan, with actual row counts, as a
    /// trailing result set: the last one that looks like a showplan document.
    /// </summary>
    private async Task<string> FetchActualPlanAsync(ExplainQueryRequest request, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        return await InRolledBackTransactionAsync(conn, ReadOnlyQuerySetupSql, ct, async tx =>
        {
            await ExecuteAsync(conn, "SET STATISTICS XML ON", tx, ct);

            await using var cmd = Command(conn, request.Sql, tx);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var plan = "";

            do
            {
                while (await reader.ReadAsync(ct))
                    if (reader.FieldCount == 1 && reader.GetFieldType(0) == typeof(string) && !reader.IsDBNull(0)
                        && reader.GetString(0) is var value && value.Contains("ShowPlanXML", StringComparison.Ordinal))
                        plan = value;
            } while (await reader.NextResultAsync(ct));

            return plan;
        });
    }

    private static async Task ExecuteAsync(DbConnection conn, string sql, DbTransaction? tx, CancellationToken ct)
    {
        await using var cmd = Command(conn, sql, tx);
        await cmd.ExecuteNonQueryAsync(ct);
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

        var runtime = Own(relOp, "RunTimeCountersPerThread").ToList();

        long? executions = runtime.Count > 0
            ? (long)runtime.Sum(e => ParseDouble((string?)e.Attribute("ActualExecutions")) ?? 0)
            : null;

        // The counters are totals over every execution and thread; the node model holds rows per
        // execution, as PostgreSQL reports them and as EstimateRows is expressed.
        double? actualRows = runtime.Count > 0
            ? runtime.Sum(e => ParseDouble((string?)e.Attribute("ActualRows")) ?? 0) / Math.Max(executions ?? 1, 1)
            : null;

        var rowsRead = runtime.Where(e => e.Attribute("ActualRowsRead") is not null).ToList();

        double? actualTimeMs = runtime.Count > 0
            ? runtime.Max(e => ParseDouble((string?)e.Attribute("ActualElapsedms")) ?? 0)
            : null;

        var objectElement = Own(relOp, "Object").FirstOrDefault();

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
            SpilledToDisk: Own(relOp, "SpillToTempDb").Any(),
            Filter: (string?)Own(relOp, "Predicate").FirstOrDefault()
                ?.Descendants(ShowPlanNs + "ScalarOperator").FirstOrDefault()?.Attribute("ScalarString"),
            Children: children,
            RowsRead: rowsRead.Count > 0
                ? rowsRead.Sum(e => ParseDouble((string?)e.Attribute("ActualRowsRead")) ?? 0)
                : null);
    }

    /// <summary>Elements that belong to this operator rather than to one nested under it.</summary>
    private static IEnumerable<XElement> Own(XElement relOp, string name) =>
        relOp.Descendants(ShowPlanNs + name)
            .Where(e => e.Ancestors(ShowPlanNs + "RelOp").FirstOrDefault() == relOp);

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
