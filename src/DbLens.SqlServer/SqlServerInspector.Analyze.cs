using System.Data.Common;
using System.Globalization;

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
            + ISNULL(u.user_lookups, 0) AS scans
        FROM sys.indexes i
        JOIN sys.tables t   ON t.object_id  = i.object_id
        JOIN sys.schemas sch ON sch.schema_id = t.schema_id
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

    // A restore as a new database sets create_date; RESTORE … WITH REPLACE over an existing one does
    // not, and only msdb's restore history records it. All three times are server-local, like GETDATE().
    private const string StatsWindowSql = """
        SELECT
            CAST(DATEDIFF_BIG(SECOND, si.sqlserver_start_time, GETDATE()) AS FLOAT) / 86400.0 AS server_start_days,
            CAST(DATEDIFF_BIG(SECOND, d.create_date, GETDATE()) AS FLOAT) / 86400.0 AS created_days,
            {0} AS restored_days,
            CAST(NULL AS FLOAT) AS stats_reset_days,
            CAST({1} AS BIT) AS restore_visible,
            CAST(CASE WHEN EXISTS (
                SELECT 1 FROM sys.dm_db_index_usage_stats u
                WHERE u.database_id = DB_ID() AND u.user_seeks + u.user_scans + u.user_lookups > 0)
                THEN 1 ELSE 0 END AS BIT) AS any_index_scanned
        FROM sys.dm_os_sys_info si
        CROSS JOIN sys.databases d
        WHERE d.database_id = DB_ID()
        """;

    private const string RestoredDaysSql = """
        (SELECT CAST(DATEDIFF_BIG(SECOND, MAX(rh.restore_date), GETDATE()) AS FLOAT) / 86400.0
         FROM msdb.dbo.restorehistory rh
         WHERE rh.destination_database_name = DB_NAME())
        """;

    protected override DbCommand IndexUsageCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables) =>
        ScopedCommand(conn, IndexUsageStatsSql, tables.Select(t => (t.Schema, t.Name)));

    protected override async Task<DbCommand> StatsWindowCommandAsync(DbConnection conn, CancellationToken ct)
    {
        await using var check = Command(conn, "SELECT HAS_PERMS_BY_NAME('msdb.dbo.restorehistory', 'OBJECT', 'SELECT')");
        var canReadHistory = await check.ExecuteScalarAsync(ct) is 1;

        return Command(conn, string.Format(CultureInfo.InvariantCulture, StatsWindowSql,
            canReadHistory ? RestoredDaysSql : "CAST(NULL AS FLOAT)",
            canReadHistory ? 1 : 0));
    }
}
