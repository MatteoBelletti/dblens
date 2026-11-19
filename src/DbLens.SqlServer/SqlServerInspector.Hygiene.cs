using System.Data.Common;

namespace DbLens.SqlServer;

public sealed partial class SqlServerInspector
{
    // SQL Server has no per-column pg_stats: the histogram of a statistics object led by the column is
    // the nearest thing. Its keys are values of the data, so only their count and whether they are
    // NULL are read. A column of NULLs gets a histogram of no steps at all, hence OUTER APPLY, and an
    // empty table one too, hence rows > 0. A column nobody filtered on or indexed has no statistics.
    // A filtered statistic sees part of the table, so it is left out; when several objects lead with
    // the column, it is all NULL or constant only if every one of them says so.
    private const string ColumnStatisticsSql = """
        SELECT schema_name, table_name, column_name,
               CAST(MIN(all_null) AS bit) AS all_null, CAST(MIN(single_value) AS bit) AS single_value
        FROM (
            SELECT sch.name AS schema_name, t.name AS table_name, c.name AS column_name,
                   CASE WHEN SUM(CASE WHEN h.range_high_key IS NOT NULL THEN 1 ELSE 0 END) = 0 THEN 1 ELSE 0 END AS all_null,
                   CASE WHEN SUM(CASE WHEN h.range_high_key IS NOT NULL THEN 1 ELSE 0 END) = 1
                             AND SUM(CASE WHEN h.range_high_key IS NOT NULL THEN h.range_rows ELSE 0 END) = 0
                        THEN 1 ELSE 0 END AS single_value
            FROM sys.stats s
            JOIN sys.stats_columns sc ON sc.object_id = s.object_id AND sc.stats_id = s.stats_id AND sc.stats_column_id = 1
            JOIN sys.columns c ON c.object_id = sc.object_id AND c.column_id = sc.column_id
            JOIN sys.tables t ON t.object_id = s.object_id
            JOIN sys.schemas sch ON sch.schema_id = t.schema_id
            CROSS APPLY sys.dm_db_stats_properties(s.object_id, s.stats_id) sp
            OUTER APPLY sys.dm_db_stats_histogram(s.object_id, s.stats_id) h
            WHERE sp.rows > 0 AND s.has_filter = 0
              AND EXISTS (
                SELECT 1 FROM OPENJSON(@pairs)
                WITH (s NVARCHAR(128) '$.s', n NVARCHAR(128) '$.n') AS p
                WHERE p.s = sch.name AND p.n = t.name
            )
            GROUP BY sch.name, t.name, c.name, s.stats_id
        ) per_statistic
        GROUP BY schema_name, table_name, column_name
        """;

    // A heap's or clustered index's leaf rows are the table's rows. The counter restarts whenever the
    // table's metadata leaves the cache, not only with the server: a zero is not "never updated".
    private const string TableUpdatesSql = """
        SELECT sch.name AS schema_name, t.name AS table_name, NULLIF(SUM(os.leaf_update_count), 0) AS updates
        FROM sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL) os
        JOIN sys.tables t ON t.object_id = os.object_id
        JOIN sys.schemas sch ON sch.schema_id = t.schema_id
        WHERE os.index_id IN (0, 1)
          AND EXISTS (
            SELECT 1 FROM OPENJSON(@pairs)
            WITH (s NVARCHAR(128) '$.s', n NVARCHAR(128) '$.n') AS p
            WHERE p.s = sch.name AND p.n = t.name
        )
        GROUP BY sch.name, t.name
        """;

    protected override DbCommand ColumnStatisticsCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables) =>
        ScopedCommand(conn, ColumnStatisticsSql, tables.Select(t => (t.Schema, t.Name)));

    protected override DbCommand TableUpdatesCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables) =>
        ScopedCommand(conn, TableUpdatesSql, tables.Select(t => (t.Schema, t.Name)));
}
