using DbLens.Core.Analysis.HotQueries;
using DbLens.Core.Inspection;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    private const string HotQueriesAvailableSql = """
        SELECT current_setting('shared_preload_libraries') LIKE '%pg_stat_statements%' AS preloaded,
               EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_stat_statements') AS created,
               pg_has_role(current_user, 'pg_read_all_stats', 'member') AS sees_all,
               current_setting('server_version_num')::int >= 140000 AS has_info
        """;

    // Statistics accumulate per database since the last pg_stat_statements_reset() (or since a crash).
    private const string HotQueriesSql = """
        SELECT s.queryid::text                         AS query_id,
               s.query                                 AS query_text,
               s.calls                                 AS calls,
               s.total_exec_time                       AS total_time_ms,
               s.rows                                  AS rows_returned,
               s.shared_blks_hit + s.shared_blks_read  AS pages_read,
               s.shared_blks_read                      AS pages_from_disk,
               s.temp_blks_written                     AS temp_pages
        FROM pg_stat_statements s
        WHERE s.dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
          AND s.query <> '<insufficient privilege>'
        ORDER BY s.total_exec_time DESC
        LIMIT @fetch
        """;

    private const string HotQueriesWindowSql =
        "SELECT EXTRACT(EPOCH FROM (NOW() - stats_reset)) / 86400.0 FROM pg_stat_statements_info";

    protected override async Task<HotQuerySource> QueryHotQueriesAsync(int fetch, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        var (preloaded, created, seesAll, hasInfo) = (await ReadAllAsync(Command(conn, HotQueriesAvailableSql), r => (
            r.Flag("preloaded"), r.Flag("created"), r.Flag("sees_all"), r.Flag("has_info")), ct)).Single();

        if (!preloaded || !created)
            return new HotQuerySource(null, null, [],
            [
                "pg_stat_statements is " + (preloaded ? "loaded but not created in this database" : "not loaded") +
                ", so PostgreSQL keeps no statistics on queries. It needs shared_preload_libraries = 'pg_stat_statements' " +
                "and a server restart, then CREATE EXTENSION pg_stat_statements in this database: a decision for the DBA. " +
                "A dump does not carry these statistics; read them on the production server or a replica.",
            ]);

        var command = Command(conn, HotQueriesSql);
        var fetchParameter = command.CreateParameter();
        fetchParameter.ParameterName = "fetch";
        fetchParameter.Value = fetch;
        command.Parameters.Add(fetchParameter);

        var queries = await ReadAllAsync(command, r => new HotQueryStats(
            r.Text("query_id"), r.Text("query_text"), r.Number("calls"), r.RealOrNull("total_time_ms") ?? 0,
            r.Number("rows_returned"), r.Number("pages_read"), r.Number("pages_from_disk"), r.NumberOrNull("temp_pages")), ct);

        double? window = null;
        if (hasInfo)
        {
            await using var windowCommand = Command(conn, HotQueriesWindowSql);
            if (await windowCommand.ExecuteScalarAsync(ct) is { } days and not DBNull)
                window = Convert.ToDouble(days, System.Globalization.CultureInfo.InvariantCulture);
        }

        return new HotQuerySource("pg_stat_statements", window, queries,
            seesAll
                ? []
                : ["This login is not in pg_read_all_stats (part of pg_monitor), so only its own queries are visible."]);
    }
}
