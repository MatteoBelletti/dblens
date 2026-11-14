using DbLens.Core.Analysis.Fragmentation;
using Npgsql;
using NpgsqlTypes;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    private const string PgStatTupleAvailableSql =
        "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pgstattuple')";

    // pgstatindex fully scans the index it is given, so it is only invoked for indexes that
    // clear the page-count threshold. Smaller indexes are still returned (with null stats) so
    // the engine can report how many were skipped. The NULL argument is what keeps the call from
    // running — pgstatindex is STRICT — whereas a WHERE beside a function in FROM only filters
    // its rows after it has been called. relkind 'i' leaves out the index of a partitioned table,
    // which has no pages of its own.
    private const string IndexFragmentationSql = """
        WITH candidates AS (
            SELECT
                ci.oid                                                            AS index_oid,
                n.nspname                                                         AS schema_name,
                ct.relname                                                        AS table_name,
                ci.relname                                                        AS index_name,
                ix.indisprimary                                                   AS is_primary,
                am.amname                                                         AS method,
                (pg_relation_size(ci.oid)
                    / current_setting('block_size')::bigint)                      AS page_count
            FROM pg_index ix
            JOIN pg_class ct     ON ct.oid = ix.indrelid
            JOIN pg_class ci     ON ci.oid = ix.indexrelid
            JOIN pg_namespace n  ON n.oid = ct.relnamespace
            JOIN pg_am am        ON am.oid = ci.relam
            WHERE am.amname = 'btree'
            AND ci.relkind = 'i'
            AND ci.relpersistence <> 't'
            AND (n.nspname, ct.relname) IN (
                SELECT * FROM unnest(@schemas::text[], @names::text[])
            )
        )
        SELECT
            c.schema_name,
            c.table_name,
            c.index_name,
            c.is_primary,
            c.method,
            c.page_count,
            st.leaf_fragmentation,
            st.avg_leaf_density
        FROM candidates c
        LEFT JOIN LATERAL pgstatindex(
            CASE WHEN c.page_count >= @min_pages THEN c.index_oid::regclass END
        ) st ON true
        ORDER BY c.schema_name, c.table_name, c.index_name
        """;

    protected override async Task<IndexFragmentationStats> QueryFragmentationStatsAsync(
        IReadOnlyList<ScopedTable> tables,
        AnalyzeFragmentationRequest request,
        CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var probe = conn.CreateCommand())
        {
            probe.CommandText = PgStatTupleAvailableSql;
            probe.CommandTimeout = QueryTimeoutSeconds;
            var available = await probe.ExecuteScalarAsync(ct) as bool? ?? false;

            if (!available)
            {
                return new IndexFragmentationStats(
                    Indexes: [],
                    StatsAvailable: false,
                    UnavailableReason:
                        "PostgreSQL does not expose index fragmentation natively; dblens reads it via the " +
                        "'pgstattuple' extension, which is not installed on this database. " +
                        "Enable it with: CREATE EXTENSION pgstattuple; " +
                        "(requires superuser or a role with CREATE on the database). " +
                        "No fragmentation findings were produced.",
                    SourceDescription: "pgstattuple extension not installed.");
            }
        }

        var schemas = tables.Select(t => t.Schema).ToArray();
        var names = tables.Select(t => t.Name).ToArray();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IndexFragmentationSql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        cmd.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas });
        cmd.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names });
        cmd.Parameters.Add(new NpgsqlParameter("min_pages", NpgsqlDbType.Bigint) { Value = request.MinPageCount });

        var indexes = new List<IndexFragmentation>();

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var fragOrdinal = r.GetOrdinal("leaf_fragmentation");
            var densityOrdinal = r.GetOrdinal("avg_leaf_density");

            indexes.Add(new IndexFragmentation(
                Schema: r.GetString(r.GetOrdinal("schema_name")),
                Table: r.GetString(r.GetOrdinal("table_name")),
                IndexName: r.GetString(r.GetOrdinal("index_name")),
                FragmentationPercent: ReadPercent(r, fragOrdinal) ?? 0,
                PageCount: r.GetInt64(r.GetOrdinal("page_count")),
                PageDensityPercent: request.IncludePageDensity ? ReadPercent(r, densityOrdinal) : null,
                IsPrimary: r.GetBoolean(r.GetOrdinal("is_primary")),
                Method: r.GetString(r.GetOrdinal("method"))));
        }

        return new IndexFragmentationStats(
            Indexes: indexes,
            StatsAvailable: true,
            UnavailableReason: null,
            SourceDescription:
                "Fragmentation read via pgstattuple's pgstatindex(), which performs a full scan of each " +
                "index it measures. Only btree indexes are supported — other access methods " +
                "(gin, gist, hash, brin) are not reported.");
    }

    /// <summary>
    /// pgstatindex returns NaN for degenerate cases such as an empty index; those are not
    /// meaningful percentages and are treated as absent.
    /// </summary>
    private static double? ReadPercent(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetDouble(ordinal);
        return double.IsNaN(value) || double.IsInfinity(value) ? null : value;
    }
}
