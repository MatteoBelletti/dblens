using System.Data.Common;
using DbLens.Core.Analysis;
using DbLens.Core.Analysis.HotQueries;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Inspection;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    // pg_settings hides shared_preload_libraries from a login outside pg_read_all_settings: unknown is
    // taken as loaded, and reading the view says the rest. pg_stat_statements_info came with 1.9.
    private const string HotQueriesAvailableSql = """
        SELECT COALESCE((SELECT setting LIKE '%pg_stat_statements%' FROM pg_settings
                         WHERE name = 'shared_preload_libraries'), true) AS preloaded,
               pg_has_role(current_user, 'pg_read_all_stats', 'member') AS sees_all,
               COALESCE((SELECT string_to_array(extversion, '.')::int[] >= ARRAY[1, 9] FROM pg_extension
                         WHERE extname = 'pg_stat_statements'), false) AS has_info
        """;

    // Statistics accumulate per database since the last pg_stat_statements_reset() (or since a crash).
    // A query has one row per user and nesting level: summed per queryid. {0}: the extension's schema.
    private const string HotQueriesSql = """
        SELECT s.queryid::text                              AS query_id,
               min(s.query)                                 AS query_text,
               sum(s.calls)                                 AS calls,
               sum(s.total_exec_time)                       AS total_time_ms,
               sum(s.rows)                                  AS rows_returned,
               sum(s.shared_blks_hit + s.shared_blks_read)  AS pages_read,
               sum(s.shared_blks_read)                      AS pages_from_disk,
               sum(s.temp_blks_written)                     AS temp_pages,
               NULL::text                                   AS plan_key
        FROM {0}.pg_stat_statements s
        WHERE s.dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
          AND s.query <> '<insufficient privilege>'
        GROUP BY s.queryid
        ORDER BY sum(s.total_exec_time) DESC
        LIMIT @fetch
        """;

    private const string HotQueriesWindowSql =
        "SELECT EXTRACT(EPOCH FROM (NOW() - stats_reset)) / 86400.0 FROM {0}.pg_stat_statements_info";

    protected override async Task<HotQueryCommands> HotQueryCommandsAsync(DbConnection conn, CancellationToken ct)
    {
        var (preloaded, seesAll, hasInfo) = (await ReadAllAsync(Command(conn, HotQueriesAvailableSql), r => (
            r.Flag("preloaded"), r.Flag("sees_all"), r.Flag("has_info")), ct)).Single();
        var schema = await StatementsSchemaAsync(conn, ct);

        if (!preloaded || schema is null)
            return new HotQueryCommands(null, null, null,
            [
                "pg_stat_statements is " + (preloaded ? "loaded but not created in this database" : "not loaded") +
                ", so PostgreSQL keeps no statistics on queries. It needs shared_preload_libraries = 'pg_stat_statements' " +
                "and a server restart, then CREATE EXTENSION pg_stat_statements in this database: a decision for the DBA. " +
                "A dump does not carry these statistics; read them on the production server or a replica.",
            ]);

        return new HotQueryCommands("pg_stat_statements", StatementsCommand(conn, HotQueriesSql, schema),
            hasInfo ? StatementsCommand(conn, HotQueriesWindowSql, schema) : null,
            seesAll ? [] : ["This login is not in pg_read_all_stats (part of pg_monitor), so only its own queries are visible."]);
    }

    // pg_stat_statements stores queries normalized, $1 for each constant. Npgsql would bind any $n as a
    // value, and a plan for "x = NULL" is no plan of the query, so the text is prepared on the server
    // and explained under force_generic_plan (PostgreSQL 12+): the plan for any value, chosen without one.
    // EXPLAIN without ANALYZE runs nothing; the statement is deallocated before the connection goes back.
    protected override async Task<(QueryPlanNode? Root, string? Caveat)> PlanHotQueryAsync(
        HotQueryStats query, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        var arguments = PlaceholderCount(query.Text);
        var prepared = false;

        try
        {
            return (ParsePlan(await InRolledBackTransactionAsync(conn, ReadOnlyQuerySetupSql, ct, async tx =>
            {
                await using (var setup = Command(conn, "SET LOCAL plan_cache_mode = force_generic_plan", tx))
                    await setup.ExecuteNonQueryAsync(ct);
                await using (var prepare = Command(conn, $"PREPARE dblens_hot_query AS {query.Text}", tx))
                    await prepare.ExecuteNonQueryAsync(ct);
                prepared = true;

                var execute = arguments == 0 ? "" : $"({string.Join(", ", Enumerable.Repeat("NULL", arguments))})";
                await using var explain = Command(conn, $"EXPLAIN (COSTS, VERBOSE, FORMAT JSON) EXECUTE dblens_hot_query{execute}", tx);
                return (await explain.ExecuteScalarAsync(ct))?.ToString() ?? "[]";
            })), null);
        }
        finally
        {
            // A prepared statement outlives the rollback; only this one is freed, not the driver's own.
            if (prepared)
            {
                await using var deallocate = Command(conn, "DEALLOCATE dblens_hot_query");
                await deallocate.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>The highest $n in the code, so literals and names that hold "$1" are not counted.</summary>
    private static int PlaceholderCount(string sql) =>
        SqlText.Tokens(sql, DbLens.Core.Models.DatabaseDialect.PostgreSql)
            .Where(t => t.Kind == SqlTokenKind.Parameter)
            .Select(t => int.Parse(t.Text[1..], System.Globalization.CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max();
}
