using System.Data.Common;

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
            + ISNULL(u.user_lookups, 0) AS scans,
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

    protected override DbCommand IndexUsageCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables) =>
        ScopedCommand(conn, IndexUsageStatsSql, tables.Select(t => (t.Schema, t.Name)));
}
