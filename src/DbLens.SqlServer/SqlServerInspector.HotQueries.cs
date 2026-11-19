using System.Data.Common;
using DbLens.Core.Analysis.HotQueries;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Inspection;

namespace DbLens.SqlServer;

public sealed partial class SqlServerInspector
{
    private const string QueryStoreStateSql = """
        SELECT actual_state_desc AS state,
               HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') AS server_state
        FROM sys.database_query_store_options
        """;

    // Runtime stats are kept per plan and interval; summed per query. query_sql_text is nvarchar(max),
    // which cannot be grouped on, hence the derived table. The stored plan is the query's latest.
    private const string QueryStoreSql = """
        SELECT TOP (@fetch)
               CAST(x.query_id AS nvarchar(20)) AS query_id,
               qt.query_sql_text                AS query_text,
               x.calls, x.total_time_ms, x.rows_returned, x.pages_read, x.pages_from_disk, x.temp_pages,
               (SELECT TOP (1) p.query_plan FROM sys.query_store_plan p
                WHERE p.query_id = x.query_id ORDER BY p.last_execution_time DESC) AS stored_plan
        FROM (
            SELECT q.query_id, q.query_text_id,
                   SUM(rs.count_executions)                                AS calls,
                   SUM(rs.avg_duration * rs.count_executions) / 1000.0    AS total_time_ms,
                   SUM(rs.avg_rowcount * rs.count_executions)             AS rows_returned,
                   SUM(rs.avg_logical_io_reads * rs.count_executions)     AS pages_read,
                   SUM(rs.avg_physical_io_reads * rs.count_executions)    AS pages_from_disk,
                   SUM(rs.avg_tempdb_space_used * rs.count_executions)    AS temp_pages
            FROM sys.query_store_query q
            JOIN sys.query_store_plan p ON p.query_id = q.query_id
            JOIN sys.query_store_runtime_stats rs ON rs.plan_id = p.plan_id
            GROUP BY q.query_id, q.query_text_id
        ) x
        JOIN sys.query_store_query_text qt ON qt.query_text_id = x.query_text_id
        ORDER BY x.total_time_ms DESC
        """;

    private const string QueryStoreWindowSql = """
        SELECT CAST(DATEDIFF_BIG(SECOND, MIN(first_execution_time), SYSDATETIMEOFFSET()) AS FLOAT) / 86400.0
        FROM sys.query_store_runtime_stats
        """;

    // The plan cache holds what ran since the plan was compiled: a restart or memory pressure empties it.
    // One query run with different literals has one entry per plan, all with its query_hash: they are
    // summed, and the text and plan are those of its costliest entry.
    private const string PlanCacheSql = """
        WITH s AS (
            SELECT qs.query_hash, qs.sql_handle, qs.plan_handle, qs.statement_start_offset, qs.statement_end_offset,
                   SUM(qs.execution_count)              OVER (PARTITION BY qs.query_hash) AS calls,
                   SUM(qs.total_elapsed_time) OVER (PARTITION BY qs.query_hash) / 1000.0 AS total_time_ms,
                   SUM(qs.total_rows)                   OVER (PARTITION BY qs.query_hash) AS rows_returned,
                   SUM(qs.total_logical_reads)          OVER (PARTITION BY qs.query_hash) AS pages_read,
                   SUM(qs.total_physical_reads)         OVER (PARTITION BY qs.query_hash) AS pages_from_disk,
                   ROW_NUMBER() OVER (PARTITION BY qs.query_hash ORDER BY qs.total_elapsed_time DESC) AS costliest
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY (SELECT CONVERT(int, value) AS dbid FROM sys.dm_exec_plan_attributes(qs.plan_handle)
                         WHERE attribute = 'dbid') a
            WHERE a.dbid = DB_ID()
        )
        SELECT TOP (@fetch)
               CONVERT(varchar(18), s.query_hash, 1) AS query_id,
               SUBSTRING(st.text, s.statement_start_offset / 2 + 1,
                   (CASE s.statement_end_offset WHEN -1 THEN DATALENGTH(st.text) ELSE s.statement_end_offset END
                    - s.statement_start_offset) / 2 + 1) AS query_text,
               s.calls, s.total_time_ms, s.rows_returned, s.pages_read, s.pages_from_disk,
               CAST(NULL AS bigint)                  AS temp_pages,
               qp.query_plan                         AS stored_plan
        FROM s
        CROSS APPLY sys.dm_exec_sql_text(s.sql_handle) st
        CROSS APPLY sys.dm_exec_text_query_plan(s.plan_handle, s.statement_start_offset, s.statement_end_offset) qp
        WHERE s.costliest = 1
        ORDER BY s.total_time_ms DESC
        """;

    private const string PlanCacheWindowSql = """
        SELECT CAST(DATEDIFF_BIG(SECOND, MIN(qs.creation_time), GETDATE()) AS FLOAT) / 86400.0
        FROM sys.dm_exec_query_stats qs
        CROSS APPLY (SELECT CONVERT(int, value) AS dbid FROM sys.dm_exec_plan_attributes(qs.plan_handle)
                     WHERE attribute = 'dbid') a
        WHERE a.dbid = DB_ID()
        """;

    protected override async Task<HotQuerySource> QueryHotQueriesAsync(int fetch, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        var (queryStore, serverState) = (await ReadAllAsync(Command(conn, QueryStoreStateSql), r => (
            r.Text("state"), r.Number("server_state") == 1), ct)).Single();

        List<string> missing = [];

        if (queryStore is "READ_WRITE" or "READ_ONLY")
            return new HotQuerySource("Query Store", await WindowAsync(conn, QueryStoreWindowSql, ct),
                await ReadHotQueriesAsync(conn, QueryStoreSql, fetch, ct), missing);

        missing.Add(
            $"Query Store is {queryStore}, so there is no history of past queries: ALTER DATABASE … SET QUERY_STORE = ON " +
            "turns it on, and a backup restored elsewhere then carries it.");

        if (!serverState)
        {
            missing.Add("The plan cache needs VIEW SERVER STATE, which this login does not have.");
            return new HotQuerySource(null, null, [], missing);
        }

        return new HotQuerySource("plan cache", await WindowAsync(conn, PlanCacheWindowSql, ct),
            await ReadHotQueriesAsync(conn, PlanCacheSql, fetch, ct), missing);
    }

    private static async Task<List<HotQueryStats>> ReadHotQueriesAsync(DbConnection conn, string sql, int fetch, CancellationToken ct)
    {
        var command = Command(conn, sql);
        var parameter = command.CreateParameter();
        parameter.ParameterName = "fetch";
        parameter.Value = fetch;
        command.Parameters.Add(parameter);

        return await ReadAllAsync(command, r => new HotQueryStats(
            // An encrypted module's text reads as NULL.
            r.Text("query_id"), r.TextOrNull("query_text") ?? "", r.Number("calls"), r.RealOrNull("total_time_ms") ?? 0,
            r.Number("rows_returned"), r.Number("pages_read"), r.Number("pages_from_disk"), r.NumberOrNull("temp_pages"),
            r.TextOrNull("stored_plan")), ct);
    }

    private static async Task<double?> WindowAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var command = Command(conn, sql);
        return await command.ExecuteScalarAsync(ct) is { } days and not DBNull
            ? Convert.ToDouble(days, System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>The plan Query Store or the plan cache stored with the query: nothing is compiled or run.</summary>
    protected override Task<(QueryPlanNode? Root, string? Caveat)> PlanHotQueryAsync(HotQueryStats query, CancellationToken ct) =>
        Task.FromResult(query.StoredPlan is { Length: > 0 } xml
            ? (ParsePlan(xml), (string?)null)
            : ((QueryPlanNode?)null, $"No plan is stored for query {query.QueryId}."));
}
