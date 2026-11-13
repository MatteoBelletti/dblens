using DbLens.Core.Analysis.Cascade;
using Npgsql;
using NpgsqlTypes;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    private const string CascadeFksSql = """
        SELECT
            n_from.nspname                                        AS from_schema,
            ct_from.relname                                       AS from_table,
            n_to.nspname                                          AS to_schema,
            ct_to.relname                                         AS to_table,
            con.conname                                           AS constraint_name,
            array_agg(a_from.attname ORDER BY kord.n)             AS from_columns,
            array_agg(a_to.attname   ORDER BY kord.n)             AS to_columns,
            CASE con.confdeltype
                WHEN 'a' THEN 'NO ACTION'
                WHEN 'r' THEN 'RESTRICT'
                WHEN 'c' THEN 'CASCADE'
                WHEN 'n' THEN 'SET NULL'
                WHEN 'd' THEN 'SET DEFAULT'
            END                                                   AS on_delete
        FROM pg_constraint con
        JOIN pg_class ct_from ON ct_from.oid = con.conrelid
        JOIN pg_namespace n_from ON n_from.oid = ct_from.relnamespace
        JOIN pg_class ct_to ON ct_to.oid = con.confrelid
        JOIN pg_namespace n_to ON n_to.oid = ct_to.relnamespace
        CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS kord(attnum, n)
        JOIN pg_attribute a_from
            ON a_from.attrelid = con.conrelid AND a_from.attnum = kord.attnum
        JOIN pg_attribute a_to
            ON a_to.attrelid = con.confrelid AND a_to.attnum = (con.confkey[kord.n::int])
        WHERE con.contype = 'f'
        AND n_from.nspname NOT LIKE 'pg\_%' ESCAPE '\'
        AND n_from.nspname <> 'information_schema'
        AND (@schema_filter IS NULL OR n_from.nspname = @schema_filter OR n_to.nspname = @schema_filter)
        GROUP BY con.conname, n_from.nspname, ct_from.relname, n_to.nspname, ct_to.relname, con.confdeltype
        ORDER BY n_from.nspname, ct_from.relname, con.conname
        """;

    private const string TableRowCountsSql = """
        SELECT
            n.nspname AS schema_name,
            c.relname AS table_name,
            CASE WHEN c.reltuples < 0 THEN NULL ELSE c.reltuples::bigint END AS approx_rows
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind = 'r'
        AND n.nspname NOT LIKE 'pg\_%' ESCAPE '\'
        AND n.nspname <> 'information_schema'
        AND (@schema_filter IS NULL OR n.nspname = @schema_filter)
        """;

    protected override async Task<CascadeGraph> QueryCascadeGraphAsync(string? schema, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        string databaseName;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT current_database()";
            cmd.CommandTimeout = QueryTimeoutSeconds;
            databaseName = (string?)await cmd.ExecuteScalarAsync(ct) ?? "unknown";
        }

        var edges = await QueryFkEdgesAsync(conn, schema, ct);
        var rowCounts = await QueryRowCountsAsync(conn, schema, ct);

        return new CascadeGraph(databaseName, edges, rowCounts);
    }

    private async Task<List<CascadeAnalysisEngine.FkEdge>> QueryFkEdgesAsync(
        NpgsqlConnection conn,
        string? schemaFilter,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = CascadeFksSql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        cmd.Parameters.Add(new NpgsqlParameter("schema_filter", NpgsqlDbType.Text)
            { Value = (object?)schemaFilter ?? DBNull.Value });

        var edges = new List<CascadeAnalysisEngine.FkEdge>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            edges.Add(new CascadeAnalysisEngine.FkEdge(
                FromSchema: r.GetString(r.GetOrdinal("from_schema")),
                FromTable: r.GetString(r.GetOrdinal("from_table")),
                ConstraintName: r.GetString(r.GetOrdinal("constraint_name")),
                FromColumns: r.GetFieldValue<string[]>(r.GetOrdinal("from_columns")),
                ToSchema: r.GetString(r.GetOrdinal("to_schema")),
                ToTable: r.GetString(r.GetOrdinal("to_table")),
                ToColumns: r.GetFieldValue<string[]>(r.GetOrdinal("to_columns")),
                OnDelete: r.GetString(r.GetOrdinal("on_delete"))));
        }

        return edges;
    }

    private async Task<Dictionary<(string Schema, string Name), long?>> QueryRowCountsAsync(
        NpgsqlConnection conn,
        string? schemaFilter,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = TableRowCountsSql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        cmd.Parameters.Add(new NpgsqlParameter("schema_filter", NpgsqlDbType.Text)
            { Value = (object?)schemaFilter ?? DBNull.Value });

        var counts = new Dictionary<(string, string), long?>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(r.GetOrdinal("schema_name"));
            var table = r.GetString(r.GetOrdinal("table_name"));
            var rows = r.IsDBNull(r.GetOrdinal("approx_rows"))
                ? (long?)null
                : r.GetInt64(r.GetOrdinal("approx_rows"));
            counts[(schema, table)] = rows;
        }

        return counts;
    }

}
