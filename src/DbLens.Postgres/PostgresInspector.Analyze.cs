using System.Data.Common;
using System.Globalization;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    private const string IndexUsageStatsSql = """
        SELECT
            s.schemaname       AS schema_name,
            s.relname          AS table_name,
            s.indexrelname     AS index_name,
            s.idx_scan         AS scans
        FROM pg_stat_user_indexes s
        WHERE (s.schemaname, s.relname) IN (
            SELECT * FROM unnest(@schemas::text[], @names::text[])
        )
        """;

    // PostgreSQL records no creation time for a database; the PG_VERSION file CREATE DATABASE writes
    // carries one, but reading it needs EXECUTE on pg_stat_file. Statistics survive a clean restart
    // since PostgreSQL 15, so the server start is a conservative bound, never a late one.
    private const string StatsWindowSql = """
        SELECT
            EXTRACT(EPOCH FROM (NOW() - pg_postmaster_start_time())) / 86400.0 AS server_start_days,
            {0} AS created_days,
            NULL::float8 AS restored_days,
            EXTRACT(EPOCH FROM (NOW() - d.stats_reset)) / 86400.0 AS stats_reset_days,
            {1} AS restore_visible,
            EXISTS (SELECT 1 FROM pg_stat_user_indexes WHERE idx_scan > 0) AS any_index_scanned
        FROM pg_stat_database d
        JOIN pg_database db ON db.oid = d.datid
        WHERE d.datname = current_database()
        """;

    private const string CreatedDaysSql =
        "EXTRACT(EPOCH FROM (NOW() - (pg_stat_file('base/' || db.oid || '/PG_VERSION')).modification)) / 86400.0";

    // The file is under base/ only in the default tablespace; elsewhere the creation time stays unknown.
    private const string CanReadCreationSql = """
        SELECT has_function_privilege('pg_catalog.pg_stat_file(text)', 'EXECUTE')
           AND (SELECT t.spcname FROM pg_database db JOIN pg_tablespace t ON t.oid = db.dattablespace
                WHERE db.datname = current_database()) = 'pg_default'
        """;

    protected override DbCommand IndexUsageCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables) =>
        ScopedCommand(conn, IndexUsageStatsSql, tables.Select(t => (t.Schema, t.Name)));

    protected override async Task<DbCommand> StatsWindowCommandAsync(DbConnection conn, CancellationToken ct)
    {
        await using var check = Command(conn, CanReadCreationSql);
        var canReadCreation = await check.ExecuteScalarAsync(ct) is true;

        return Command(conn, string.Format(CultureInfo.InvariantCulture, StatsWindowSql,
            canReadCreation ? CreatedDaysSql : "NULL::float8",
            canReadCreation ? "true" : "false"));
    }
}
