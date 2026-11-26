using System.Data.Common;
using DbLens.Core.Analysis.Fragmentation;
using DbLens.Core.Inspection;
using Microsoft.Data.SqlClient;

namespace DbLens.SqlServer;

public sealed partial class SqlServerInspector
{
    // Lists the indexes of the scope with their leaf pages and measures nothing: the DMF reads the index it is
    // given, so the base calls it once per index that clears the page-count threshold, by object and index id.
    private const string FragmentationCandidatesSql = """
        SELECT
            sch.name                      AS schema_name,
            t.name                        AS table_name,
            i.name                        AS index_name,
            i.is_primary_key              AS is_primary,
            i.type_desc                   AS method,
            SUM(ps.in_row_data_page_count) AS page_count,
            t.object_id                   AS handle,
            i.index_id                    AS handle2
        FROM sys.tables t
        JOIN sys.schemas sch ON sch.schema_id = t.schema_id
        JOIN sys.indexes i ON i.object_id = t.object_id
        JOIN sys.dm_db_partition_stats ps ON ps.object_id = i.object_id AND ps.index_id = i.index_id
        WHERE i.index_id > 0
        AND i.name IS NOT NULL
        AND EXISTS (
            SELECT 1 FROM OPENJSON(@pairs)
            WITH (s NVARCHAR(128) '$.s', n NVARCHAR(128) '$.n') AS p
            WHERE p.s = sch.name AND p.n = t.name
        )
        GROUP BY sch.name, t.name, i.name, i.is_primary_key, i.type_desc, t.object_id, i.index_id
        """;

    // IN_ROW_DATA only: LOB and row-overflow allocation units would otherwise emit extra rows for the same
    // index; a partitioned index is as fragmented as its worst partition.
    private const string FragmentationMeasureSql = """
        SELECT
            MAX(ps.avg_fragmentation_in_percent)     AS fragmentation_percent,
            MIN(ps.avg_page_space_used_in_percent)   AS page_density_percent
        FROM sys.dm_db_index_physical_stats(DB_ID(), @object_id, @index_id, NULL, @mode) ps
        WHERE ps.alloc_unit_type_desc = 'IN_ROW_DATA'
        """;

    protected override DbCommand FragmentationCandidatesCommand(DbConnection conn, IReadOnlyList<ScopedTable> tables) =>
        ScopedCommand(conn, FragmentationCandidatesSql, tables.Select(t => (t.Schema, t.Name)));

    // LIMITED reads only the parent level of the b-tree and is cheap, but does not report page density.
    // SAMPLED reads 1% of leaf pages, which is what page density requires.
    protected override DbCommand FragmentationMeasureCommand(DbConnection conn, FragmentationCandidate index, AnalyzeFragmentationRequest request)
    {
        var cmd = Command(conn, FragmentationMeasureSql);
        cmd.Parameters.Add(new SqlParameter("object_id", System.Data.SqlDbType.Int) { Value = (int)index.Handle });
        cmd.Parameters.Add(new SqlParameter("index_id", System.Data.SqlDbType.Int) { Value = (int)index.Handle2 });
        cmd.Parameters.Add(new SqlParameter("mode", System.Data.SqlDbType.NVarChar, 128) { Value = request.IncludePageDensity ? "SAMPLED" : "LIMITED" });
        return cmd;
    }

    protected override Task<string?> FragmentationUnavailableAsync(DbConnection conn, CancellationToken ct) => Task.FromResult<string?>(null);

    // Reading physical stats needs VIEW DATABASE STATE. Without it the measurement is unavailable rather than
    // empty, and saying so beats reporting zero findings.
    protected override string? FragmentationPermissionReason(DbException ex) =>
        ex is SqlException { Number: 229 or 262 or 297 or 300 } denied
            ? "Reading sys.dm_db_index_physical_stats requires the VIEW DATABASE STATE " +
              $"permission, which the current login does not have ({denied.Message}). " +
              "Grant it with: GRANT VIEW DATABASE STATE TO [your_login]; " +
              "No fragmentation findings were produced."
            : null;

    protected override string FragmentationSource(AnalyzeFragmentationRequest request) =>
        $"Fragmentation read via sys.dm_db_index_physical_stats in {(request.IncludePageDensity ? "SAMPLED" : "LIMITED")} mode" +
        (request.IncludePageDensity
            ? ", which samples 1% of leaf pages to obtain page density."
            : ", which reads only parent-level pages and therefore reports no page density.");
}
