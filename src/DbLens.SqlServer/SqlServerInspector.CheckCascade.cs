using DbLens.Core.Analysis.Cascade;
using Microsoft.Data.SqlClient;

namespace DbLens.SqlServer;

public sealed partial class SqlServerInspector
{
    private const string CascadeFksSql = """
        SELECT
            sc_from.name                                                AS from_schema,
            t_from.name                                                 AS from_table,
            sc_to.name                                                  AS to_schema,
            t_to.name                                                   AS to_table,
            fk.name                                                     AS constraint_name,
            STRING_AGG(c_from.name, ',')
                WITHIN GROUP (ORDER BY fkc.constraint_column_id)        AS from_columns,
            STRING_AGG(c_to.name, ',')
                WITHIN GROUP (ORDER BY fkc.constraint_column_id)        AS to_columns,
            CASE fk.delete_referential_action_desc
                WHEN 'CASCADE'      THEN 'CASCADE'
                WHEN 'SET_NULL'     THEN 'SET NULL'
                WHEN 'SET_DEFAULT'  THEN 'SET DEFAULT'
                ELSE                     'NO ACTION'
            END                                                         AS on_delete
        FROM sys.foreign_keys fk
        JOIN sys.tables t_from  ON t_from.object_id  = fk.parent_object_id
        JOIN sys.schemas sc_from ON sc_from.schema_id = t_from.schema_id
        JOIN sys.tables t_to    ON t_to.object_id    = fk.referenced_object_id
        JOIN sys.schemas sc_to  ON sc_to.schema_id   = t_to.schema_id
        JOIN sys.foreign_key_columns fkc
            ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns c_from
            ON c_from.object_id = fkc.parent_object_id
           AND c_from.column_id = fkc.parent_column_id
        JOIN sys.columns c_to
            ON c_to.object_id   = fkc.referenced_object_id
           AND c_to.column_id   = fkc.referenced_column_id
        WHERE sc_from.name NOT IN ('sys', 'INFORMATION_SCHEMA')
        AND sc_from.name NOT LIKE 'db[_]%'
        AND (@schema_filter IS NULL OR sc_from.name = @schema_filter OR sc_to.name = @schema_filter)
        GROUP BY sc_from.name, t_from.name, sc_to.name, t_to.name, fk.name,
                 fk.delete_referential_action_desc
        ORDER BY sc_from.name, t_from.name, fk.name
        """;

    private const string TableRowCountsSql = """
        SELECT
            s.name  AS schema_name,
            t.name  AS table_name,
            SUM(p.rows) AS approx_rows
        FROM sys.partitions p
        JOIN sys.tables  t ON t.object_id  = p.object_id
        JOIN sys.schemas s ON s.schema_id  = t.schema_id
        WHERE p.index_id IN (0, 1)
        AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
        AND s.name NOT LIKE 'db[_]%'
        AND (@schema_filter IS NULL OR s.name = @schema_filter)
        GROUP BY s.name, t.name
        """;

    protected override async Task<CascadeGraph> QueryCascadeGraphAsync(string? schema, CancellationToken ct)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        var edges = await QueryFkEdgesAsync(conn, schema, ct);
        var rowCounts = await QueryRowCountsAsync(conn, schema, ct);

        return new CascadeGraph(conn.Database, edges, rowCounts);
    }

    private async Task<List<CascadeAnalysisEngine.FkEdge>> QueryFkEdgesAsync(
        SqlConnection conn,
        string? schemaFilter,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = CascadeFksSql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        cmd.Parameters.Add(new SqlParameter("schema_filter", System.Data.SqlDbType.NVarChar, 128)
            { Value = (object?)schemaFilter ?? DBNull.Value });

        var edges = new List<CascadeAnalysisEngine.FkEdge>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var fromCols = r.GetString(r.GetOrdinal("from_columns")).Split(',');
            var toCols = r.GetString(r.GetOrdinal("to_columns")).Split(',');
            edges.Add(new CascadeAnalysisEngine.FkEdge(
                FromSchema: r.GetString(r.GetOrdinal("from_schema")),
                FromTable: r.GetString(r.GetOrdinal("from_table")),
                ConstraintName: r.GetString(r.GetOrdinal("constraint_name")),
                FromColumns: fromCols,
                ToSchema: r.GetString(r.GetOrdinal("to_schema")),
                ToTable: r.GetString(r.GetOrdinal("to_table")),
                ToColumns: toCols,
                OnDelete: r.GetString(r.GetOrdinal("on_delete"))));
        }

        return edges;
    }

    private async Task<Dictionary<(string Schema, string Name), long?>> QueryRowCountsAsync(
        SqlConnection conn,
        string? schemaFilter,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = TableRowCountsSql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        cmd.Parameters.Add(new SqlParameter("schema_filter", System.Data.SqlDbType.NVarChar, 128)
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
