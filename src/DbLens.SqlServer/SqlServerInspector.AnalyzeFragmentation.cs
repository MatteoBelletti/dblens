using System.Text.Json;
using DbLens.Core.Analysis.Fragmentation;
using Microsoft.Data.SqlClient;

namespace DbLens.SqlServer;

public sealed partial class SqlServerInspector
{
    // The DMF is applied per scoped table rather than with a NULL object_id, which would scan
    // every object in the database. IN_ROW_DATA only: LOB and row-overflow allocation units
    // would otherwise emit extra rows for the same index.
    private const string IndexFragmentationSql = """
        WITH scoped AS (
            SELECT t.object_id, sch.name AS schema_name, t.name AS table_name
            FROM sys.tables t
            JOIN sys.schemas sch ON sch.schema_id = t.schema_id
            WHERE EXISTS (
                SELECT 1 FROM OPENJSON(@pairs)
                WITH (s NVARCHAR(128) '$.s', n NVARCHAR(128) '$.n') AS p
                WHERE p.s = sch.name AND p.n = t.name
            )
        )
        SELECT
            s.schema_name,
            s.table_name,
            i.name                                  AS index_name,
            i.is_primary_key,
            i.type_desc                             AS method,
            ps.avg_fragmentation_in_percent,
            ps.page_count,
            ps.avg_page_space_used_in_percent
        FROM scoped s
        CROSS APPLY sys.dm_db_index_physical_stats(DB_ID(), s.object_id, NULL, NULL, @mode) ps
        JOIN sys.indexes i
            ON  i.object_id = ps.object_id
            AND i.index_id  = ps.index_id
        WHERE ps.index_id > 0
        AND ps.alloc_unit_type_desc = 'IN_ROW_DATA'
        AND i.name IS NOT NULL
        ORDER BY s.schema_name, s.table_name, i.name
        """;

    protected override async Task<IndexFragmentationStats> QueryFragmentationStatsAsync(
        IReadOnlyList<ScopedTable> tables,
        AnalyzeFragmentationRequest request,
        CancellationToken ct)
    {
        try
        {
            return await ReadFragmentationStatsAsync(tables, request, ct);
        }
        catch (SqlException ex) when (ex.Number is 297 or 300 or 229)
        {
            // Reading physical stats needs VIEW DATABASE STATE. Without it the measurement is
            // unavailable rather than empty, and saying so beats reporting zero findings.
            return new IndexFragmentationStats(
                Indexes: [],
                StatsAvailable: false,
                UnavailableReason:
                    "Reading sys.dm_db_index_physical_stats requires the VIEW DATABASE STATE " +
                    $"permission, which the current login does not have ({ex.Message}). " +
                    "Grant it with: GRANT VIEW DATABASE STATE TO [your_login]; " +
                    "No fragmentation findings were produced.",
                SourceDescription: "Permission denied on sys.dm_db_index_physical_stats.");
        }
    }

    private async Task<IndexFragmentationStats> ReadFragmentationStatsAsync(
        IReadOnlyList<ScopedTable> tables,
        AnalyzeFragmentationRequest request,
        CancellationToken ct)
    {
        var pairs = JsonSerializer.Serialize(
            tables.Select(t => new { s = t.Schema, n = t.Name }));

        // LIMITED reads only the parent level of the b-tree and is cheap, but does not report
        // page density. SAMPLED reads 1% of leaf pages, which is what page density requires.
        var mode = request.IncludePageDensity ? "SAMPLED" : "LIMITED";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = IndexFragmentationSql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        cmd.Parameters.Add(new SqlParameter("pairs", System.Data.SqlDbType.NVarChar, -1) { Value = pairs });
        cmd.Parameters.Add(new SqlParameter("mode", System.Data.SqlDbType.NVarChar, 128) { Value = mode });

        var indexes = new List<IndexFragmentation>();

        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var densityOrdinal = r.GetOrdinal("avg_page_space_used_in_percent");
            var pageCountOrdinal = r.GetOrdinal("page_count");
            var fragOrdinal = r.GetOrdinal("avg_fragmentation_in_percent");

            indexes.Add(new IndexFragmentation(
                Schema: r.GetString(r.GetOrdinal("schema_name")),
                Table: r.GetString(r.GetOrdinal("table_name")),
                IndexName: r.GetString(r.GetOrdinal("index_name")),
                FragmentationPercent: r.IsDBNull(fragOrdinal) ? 0 : r.GetDouble(fragOrdinal),
                PageCount: r.IsDBNull(pageCountOrdinal) ? 0 : r.GetInt64(pageCountOrdinal),
                PageDensityPercent: r.IsDBNull(densityOrdinal) ? null : r.GetDouble(densityOrdinal),
                IsPrimary: r.GetBoolean(r.GetOrdinal("is_primary_key")),
                Method: r.GetString(r.GetOrdinal("method"))));
        }

        return new IndexFragmentationStats(
            Indexes: indexes,
            StatsAvailable: true,
            UnavailableReason: null,
            SourceDescription:
                $"Fragmentation read via sys.dm_db_index_physical_stats in {mode} mode" +
                (request.IncludePageDensity
                    ? ", which samples 1% of leaf pages to obtain page density."
                    : ", which reads only parent-level pages and therefore reports no page density."));
    }
}
