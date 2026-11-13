using DbLens.Core.Analysis.Indexes;
using Npgsql;
using NpgsqlTypes;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    private const string IndexUsageStatsSql = """
        SELECT
            s.schemaname,
            s.relname          AS table_name,
            s.indexrelname     AS index_name,
            s.idx_scan,
            EXTRACT(EPOCH FROM (NOW() - pg_postmaster_start_time())) / 86400.0 AS uptime_days
        FROM pg_stat_user_indexes s
        WHERE (s.schemaname, s.relname) IN (
            SELECT * FROM unnest(@schemas::text[], @names::text[])
        )
        """;

    protected override async Task<IndexUsageStats> QueryIndexUsageStatsAsync(
        IReadOnlyList<ScopedTable> tables,
        CancellationToken ct)
    {
        var schemas = tables.Select(t => t.Schema).ToArray();
        var names = tables.Select(t => t.Name).ToArray();

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IndexUsageStatsSql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        cmd.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas });
        cmd.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names });

        var scanCounts = new Dictionary<(string, string, string), long>();
        double uptimeDays = 0;

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            uptimeDays = r.GetDouble(r.GetOrdinal("uptime_days"));
            scanCounts[(
                r.GetString(r.GetOrdinal("schemaname")),
                r.GetString(r.GetOrdinal("table_name")),
                r.GetString(r.GetOrdinal("index_name"))
            )] = r.GetInt64(r.GetOrdinal("idx_scan"));
        }

        return new IndexUsageStats(scanCounts, uptimeDays);
    }
}
