using System.Data.Common;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    private const string IndexUsageStatsSql = """
        SELECT
            s.schemaname       AS schema_name,
            s.relname          AS table_name,
            s.indexrelname     AS index_name,
            s.idx_scan         AS scans,
            EXTRACT(EPOCH FROM (NOW() - pg_postmaster_start_time())) / 86400.0 AS uptime_days
        FROM pg_stat_user_indexes s
        WHERE (s.schemaname, s.relname) IN (
            SELECT * FROM unnest(@schemas::text[], @names::text[])
        )
        """;

    protected override DbCommand IndexUsageCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables) =>
        ScopedCommand(conn, IndexUsageStatsSql, tables.Select(t => (t.Schema, t.Name)));
}
