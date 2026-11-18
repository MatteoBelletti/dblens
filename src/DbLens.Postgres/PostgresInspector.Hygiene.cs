using System.Data.Common;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    // null_frac and n_distinct are the planner's estimates from ANALYZE's sample: never a value.
    private const string ColumnStatisticsSql = """
        SELECT s.schemaname AS schema_name,
               s.tablename  AS table_name,
               s.attname    AS column_name,
               s.null_frac = 1                        AS all_null,
               s.null_frac < 1 AND s.n_distinct = 1   AS single_value
        FROM pg_stats s
        WHERE NOT s.inherited
          AND (s.schemaname, s.tablename) IN (SELECT * FROM unnest(@schemas::text[], @names::text[]))
        """;

    private const string TableUpdatesSql = """
        SELECT schemaname AS schema_name, relname AS table_name, n_tup_upd AS updates
        FROM pg_stat_user_tables
        WHERE (schemaname, relname) IN (SELECT * FROM unnest(@schemas::text[], @names::text[]))
        """;

    protected override DbCommand ColumnStatisticsCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables) =>
        ScopedCommand(conn, ColumnStatisticsSql, tables.Select(t => (t.Schema, t.Name)));

    protected override DbCommand TableUpdatesCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables) =>
        ScopedCommand(conn, TableUpdatesSql, tables.Select(t => (t.Schema, t.Name)));
}
