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

    // Runtime stats are kept per plan and interval: summed per query, then per query_hash, since one
    // statement run under different SET options is several queries. query_sql_text is nvarchar(max),
    // which cannot be grouped on, hence the derived tables. The plan is read later, for listed queries.
    private const string QueryStoreSql = """
        WITH per_query AS (
            SELECT q.query_id, q.query_hash, q.query_text_id,
                   SUM(rs.count_executions)                                AS calls,
                   SUM(rs.avg_duration * rs.count_executions) / 1000.0    AS total_time_ms,
                   SUM(rs.avg_rowcount * rs.count_executions)             AS rows_returned,
                   SUM(rs.avg_logical_io_reads * rs.count_executions)     AS pages_read,
                   SUM(rs.avg_physical_io_reads * rs.count_executions)    AS pages_from_disk,
                   SUM(rs.avg_tempdb_space_used * rs.count_executions)    AS temp_pages
            FROM sys.query_store_query q
            JOIN sys.query_store_plan p ON p.query_id = q.query_id
            JOIN sys.query_store_runtime_stats rs ON rs.plan_id = p.plan_id
            GROUP BY q.query_id, q.query_hash, q.query_text_id
        ), per_hash AS (
            SELECT query_id, query_text_id,
                   SUM(calls)           OVER (PARTITION BY query_hash) AS calls,
                   SUM(total_time_ms)   OVER (PARTITION BY query_hash) AS total_time_ms,
                   SUM(rows_returned)   OVER (PARTITION BY query_hash) AS rows_returned,
                   SUM(pages_read)      OVER (PARTITION BY query_hash) AS pages_read,
                   SUM(pages_from_disk) OVER (PARTITION BY query_hash) AS pages_from_disk,
                   SUM(temp_pages)      OVER (PARTITION BY query_hash) AS temp_pages,
                   ROW_NUMBER() OVER (PARTITION BY query_hash ORDER BY total_time_ms DESC) AS costliest
            FROM per_query
        )
        SELECT TOP (@fetch)
               CAST(x.query_id AS nvarchar(20)) AS query_id,
               qt.query_sql_text                AS query_text,
               x.calls, x.total_time_ms, x.rows_returned, x.pages_read, x.pages_from_disk, x.temp_pages,
               CONCAT('store:', x.query_id)     AS plan_key
        FROM per_hash x
        JOIN sys.query_store_query_text qt ON qt.query_text_id = x.query_text_id
        WHERE x.costliest = 1
        ORDER BY x.total_time_ms DESC
        """;

    private const string QueryStorePlanSql =
        "SELECT TOP (1) query_plan FROM sys.query_store_plan WHERE query_id = @id ORDER BY last_execution_time DESC";

    private const string QueryStoreWindowSql = """
        SELECT CAST(DATEDIFF_BIG(SECOND, MIN(first_execution_time), SYSDATETIMEOFFSET()) AS FLOAT) / 86400.0 AS window_days
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
               CONCAT('cache:', CONVERT(varchar(130), s.plan_handle, 1), ':',
                      s.statement_start_offset, ':', s.statement_end_offset) AS plan_key
        FROM s
        CROSS APPLY sys.dm_exec_sql_text(s.sql_handle) st
        WHERE s.costliest = 1
        ORDER BY s.total_time_ms DESC
        """;

    private const string PlanCacheWindowSql = """
        SELECT CAST(DATEDIFF_BIG(SECOND, MIN(qs.creation_time), GETDATE()) AS FLOAT) / 86400.0 AS window_days
        FROM sys.dm_exec_query_stats qs
        CROSS APPLY (SELECT CONVERT(int, value) AS dbid FROM sys.dm_exec_plan_attributes(qs.plan_handle)
                     WHERE attribute = 'dbid') a
        WHERE a.dbid = DB_ID()
        """;

    private const string CachedPlanSql =
        "SELECT query_plan FROM sys.dm_exec_text_query_plan(CONVERT(varbinary(64), @handle, 1), @start, @end)";

    protected override async Task<HotQueryCommands> HotQueryCommandsAsync(DbConnection conn, CancellationToken ct)
    {
        var (queryStore, serverState) = (await ReadAllAsync(Command(conn, QueryStoreStateSql), r => (
            r.Text("state"), r.Number("server_state") == 1), ct)).Single();

        if (queryStore is "READ_WRITE" or "READ_ONLY")
            return new HotQueryCommands("Query Store", Command(conn, QueryStoreSql), Command(conn, QueryStoreWindowSql), []);

        var off = $"Query Store is {queryStore}, so there is no history of past queries: ALTER DATABASE … SET QUERY_STORE = ON " +
                  "turns it on, and a backup restored elsewhere then carries it.";

        return serverState
            ? new HotQueryCommands("plan cache", Command(conn, PlanCacheSql), Command(conn, PlanCacheWindowSql), [off])
            : new HotQueryCommands(null, null, null, [off, "The plan cache needs VIEW SERVER STATE, which this login does not have."]);
    }

    /// <summary>The plan Query Store or the plan cache stored with the query: nothing is compiled or run.</summary>
    protected override async Task<(QueryPlanNode? Root, string? Caveat)> PlanHotQueryAsync(HotQueryStats query, CancellationToken ct)
    {
        var none = $"No plan is stored for query {query.QueryId}.";
        if (query.PlanKey?.Split(':') is not [var kind, ..] parts) return (null, none);

        await using var conn = await OpenAsync(ct);
        await using var command = kind == "store" ? Command(conn, QueryStorePlanSql) : Command(conn, CachedPlanSql);
        if (kind == "store")
            AddParameter(command, "id", long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
        else
        {
            AddParameter(command, "handle", parts[1]);
            AddParameter(command, "start", int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
            AddParameter(command, "end", int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture));
        }

        return await command.ExecuteScalarAsync(ct) is string { Length: > 0 } xml ? (ParsePlan(xml), null) : (null, none);
    }
}
