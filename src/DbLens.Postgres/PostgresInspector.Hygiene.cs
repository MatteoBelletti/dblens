using System.Data.Common;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    // null_frac and n_distinct are the planner's estimates from ANALYZE's sample: never a value. A
    // partitioned table's own statistics are its inherited ones, over all its partitions.
    private const string ColumnStatisticsSql = """
        SELECT s.schemaname AS schema_name,
               s.tablename  AS table_name,
               s.attname    AS column_name,
               s.null_frac = 1                        AS all_null,
               s.null_frac < 1 AND s.n_distinct = 1   AS single_value
        FROM pg_stats s
        JOIN pg_namespace n ON n.nspname = s.schemaname
        JOIN pg_class c ON c.relnamespace = n.oid AND c.relname = s.tablename
        WHERE s.inherited = (c.relkind = 'p')
          AND (s.schemaname, s.tablename) IN (SELECT * FROM unnest(@schemas::text[], @names::text[]))
        """;

    // A partitioned table counts nothing itself: its rows are updated in its partitions.
    private const string TableUpdatesSql = """
        SELECT n.nspname AS schema_name, c.relname AS table_name,
               pg_stat_get_tuples_updated(c.oid)
               + COALESCE((SELECT SUM(pg_stat_get_tuples_updated(pt.relid)) FROM pg_partition_tree(c.oid) pt
                           WHERE pt.relid <> c.oid), 0) AS updates
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'p')
          AND (n.nspname, c.relname) IN (SELECT * FROM unnest(@schemas::text[], @names::text[]))
        """;

    protected override DbCommand ColumnStatisticsCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables) =>
        ScopedCommand(conn, ColumnStatisticsSql, tables.Select(t => (t.Schema, t.Name)));

    protected override DbCommand TableUpdatesCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables) =>
        ScopedCommand(conn, TableUpdatesSql, tables.Select(t => (t.Schema, t.Name)));
}
