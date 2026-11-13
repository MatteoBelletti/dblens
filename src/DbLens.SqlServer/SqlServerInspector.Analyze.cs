using System.Text.Json;
using DbLens.Core.Analysis.Indexes;
using Microsoft.Data.SqlClient;

namespace DbLens.SqlServer;

public sealed partial class SqlServerInspector
{
    private const string IndexUsageStatsSql = """
        SELECT
            sch.name        AS schema_name,
            t.name          AS table_name,
            i.name          AS index_name,
            ISNULL(u.user_seeks,   0)
            + ISNULL(u.user_scans,   0)
            + ISNULL(u.user_lookups, 0) AS total_scans,
            CAST(DATEDIFF(SECOND, si.sqlserver_start_time, GETUTCDATE()) AS FLOAT) / 86400.0
                            AS uptime_days
        FROM sys.indexes i
        JOIN sys.tables t   ON t.object_id  = i.object_id
        JOIN sys.schemas sch ON sch.schema_id = t.schema_id
        CROSS JOIN sys.dm_os_sys_info si
        LEFT JOIN sys.dm_db_index_usage_stats u
            ON  u.object_id  = i.object_id
            AND u.index_id   = i.index_id
            AND u.database_id = DB_ID()
        WHERE i.type > 0
        AND EXISTS (
            SELECT 1 FROM OPENJSON(@pairs)
            WITH (s NVARCHAR(128) '$.s', n NVARCHAR(128) '$.n') AS p
            WHERE p.s = sch.name AND p.n = t.name
        )
        """;

    protected override async Task<IndexUsageStats> QueryIndexUsageStatsAsync(
        IReadOnlyList<ScopedTable> tables,
        CancellationToken ct)
    {
        var pairs = JsonSerializer.Serialize(
            tables.Select(t => new { s = t.Schema, n = t.Name }));

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IndexUsageStatsSql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        cmd.Parameters.Add(new SqlParameter("pairs", System.Data.SqlDbType.NVarChar, -1) { Value = pairs });

        var scanCounts = new Dictionary<(string, string, string), long>();
        double uptimeDays = 0;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            uptimeDays = r.GetDouble(r.GetOrdinal("uptime_days"));
            scanCounts[(
                r.GetString(r.GetOrdinal("schema_name")),
                r.GetString(r.GetOrdinal("table_name")),
                r.GetString(r.GetOrdinal("index_name"))
            )] = r.GetInt64(r.GetOrdinal("total_scans"));
        }

        return new IndexUsageStats(scanCounts, uptimeDays);
    }
}
