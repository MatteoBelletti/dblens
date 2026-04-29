using System.Diagnostics;
using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DbLens.SqlServer;

public sealed class SqlServerInspector : IDatabaseInspector
{
    private readonly string _connectionString;
    private readonly ILogger<SqlServerInspector> _logger;

    private const string SchemaOverviewSql = """
        SELECT
            s.name                              AS schema_name,
            o.name                              AS object_name,
            RTRIM(o.type)                       AS object_type,
            CASE WHEN o.type = 'U'
                 THEN (SELECT SUM(p.rows)
                       FROM sys.partitions p
                       WHERE p.object_id = o.object_id
                         AND p.index_id IN (0, 1))
                 ELSE NULL
            END                                 AS approximate_row_count,
            (SELECT COUNT(*)
             FROM sys.columns c
             WHERE c.object_id = o.object_id
            )                                   AS column_count,
            CASE WHEN EXISTS (
                     SELECT 1 FROM sys.indexes i
                     WHERE i.object_id = o.object_id AND i.is_primary_key = 1)
                 THEN CAST(1 AS BIT)
                 ELSE CAST(0 AS BIT)
            END                                 AS has_primary_key,
            (SELECT COUNT(*)
             FROM sys.foreign_keys fk
             WHERE fk.parent_object_id = o.object_id
            )                                   AS outgoing_fk_count,
            (SELECT COUNT(*)
             FROM sys.foreign_keys fk
             WHERE fk.referenced_object_id = o.object_id
            )                                   AS incoming_fk_count,
            (SELECT COUNT(*)
             FROM sys.indexes i
             WHERE i.object_id = o.object_id AND i.type > 0
            )                                   AS index_count,
            ISNULL((
                SELECT CAST(SUM(a.total_pages) AS BIGINT) * 8192
                FROM sys.partitions p
                JOIN sys.allocation_units a
                  ON (a.type IN (1, 3) AND a.container_id = p.hobt_id)
                  OR (a.type = 2      AND a.container_id = p.partition_id)
                WHERE p.object_id = o.object_id
            ), 0)                               AS total_size_bytes,
            CAST(ep.value AS NVARCHAR(MAX))     AS object_comment,
            DB_NAME()                           AS database_name
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN sys.extended_properties ep
            ON ep.major_id = o.object_id
           AND ep.minor_id = 0
           AND ep.class    = 1
           AND ep.name     = 'MS_Description'
        WHERE
            RTRIM(o.type) IN ('U', 'V', 'SV')
            AND (@include_views = 1 OR RTRIM(o.type) = 'U')
            AND (@include_system = 1
                 OR (s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
                     AND s.name NOT LIKE 'db[_]%'))
            AND (@schema_filter IS NULL OR s.name = @schema_filter)
            AND (@name_pattern  IS NULL OR o.name LIKE @name_pattern)
        ORDER BY s.name, o.name;
        """;

    public SqlServerInspector(string connectionString, ILogger<SqlServerInspector> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public DatabaseDialect Dialect => DatabaseDialect.SqlServer;

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct);
        return true;
    }

    public async Task<DatabaseSchemaOverview> GetSchemaOverviewAsync(
        SchemaOverviewRequest request,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Opening SQL Server connection for schema overview (schema={Schema}, pattern={Pattern})",
            request.SchemaFilter ?? "*", request.NamePattern ?? "*");

        var sw = Stopwatch.StartNew();

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = SchemaOverviewSql;

            cmd.Parameters.Add(new SqlParameter("schema_filter", System.Data.SqlDbType.NVarChar, 128)
                { Value = (object?)request.SchemaFilter ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("name_pattern", System.Data.SqlDbType.NVarChar, 256)
                { Value = (object?)request.NamePattern ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("include_views", System.Data.SqlDbType.Bit)
                { Value = request.IncludeViews });
            cmd.Parameters.Add(new SqlParameter("include_system", System.Data.SqlDbType.Bit)
                { Value = request.IncludeSystemSchemas });

            _logger.LogDebug(
                "Executing schema overview query: schema_filter={SchemaFilter}, name_pattern={NamePattern}, " +
                "include_views={IncludeViews}, include_system={IncludeSystem}",
                request.SchemaFilter, request.NamePattern, request.IncludeViews, request.IncludeSystemSchemas);

            var rows = new List<RawRow>();
            string? databaseName = null;

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                databaseName ??= reader.GetString(reader.GetOrdinal("database_name"));
                rows.Add(ReadRow(reader));
            }

            var schemas = rows
                .GroupBy(r => r.SchemaName)
                .OrderBy(g => g.Key)
                .Select(BuildSchemaInfo)
                .ToList();

            var totalTables = schemas.Sum(s => s.TableCount);
            var totalViews = schemas.Sum(s => s.ViewCount);
            var totalBytes = schemas.SelectMany(s => s.Objects).Sum(o => o.TotalSizeBytes);
            var approxRows = schemas.SelectMany(s => s.Objects)
                .Where(o => o.ApproximateRowCount.HasValue)
                .Sum(o => o.ApproximateRowCount!.Value);

            sw.Stop();
            _logger.LogInformation(
                "Schema overview completed in {ElapsedMs}ms: {Tables} tables, {Views} views across {SchemaCount} schema(s)",
                sw.ElapsedMilliseconds, totalTables, totalViews, schemas.Count);

            return new DatabaseSchemaOverview(
                Summary: BuildSummary(totalTables, totalViews, schemas.Count, approxRows, totalBytes),
                Dialect: "SqlServer",
                Database: databaseName ?? "unknown",
                GeneratedAt: DateTime.UtcNow.ToString("O"),
                Schemas: schemas
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex)
        {
            throw new DatabaseInspectionException(
                $"SQL Server error during schema overview: {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            throw new DatabaseInspectionException(
                $"Timeout during schema overview: {ex.Message}", ex);
        }
    }

    private static RawRow ReadRow(SqlDataReader r) => new(
        SchemaName: r.GetString(r.GetOrdinal("schema_name")),
        ObjectName: r.GetString(r.GetOrdinal("object_name")),
        ObjectKind: r.GetString(r.GetOrdinal("object_type")) is "V" or "SV"
            ? DatabaseObjectKind.View
            : DatabaseObjectKind.Table,
        ApproximateRowCount: r.IsDBNull(r.GetOrdinal("approximate_row_count"))
            ? null
            : r.GetInt64(r.GetOrdinal("approximate_row_count")),
        ColumnCount: r.GetInt32(r.GetOrdinal("column_count")),
        HasPrimaryKey: r.GetBoolean(r.GetOrdinal("has_primary_key")),
        OutgoingFkCount: r.GetInt32(r.GetOrdinal("outgoing_fk_count")),
        IncomingFkCount: r.GetInt32(r.GetOrdinal("incoming_fk_count")),
        IndexCount: r.GetInt32(r.GetOrdinal("index_count")),
        TotalSizeBytes: r.GetInt64(r.GetOrdinal("total_size_bytes")),
        Comment: r.IsDBNull(r.GetOrdinal("object_comment"))
            ? null
            : r.GetString(r.GetOrdinal("object_comment"))
    );

    private static SchemaInfo BuildSchemaInfo(IGrouping<string, RawRow> group)
    {
        var objects = group
            .Select(r => new DatabaseObject(
                Name: r.ObjectName,
                Kind: r.ObjectKind,
                ApproximateRowCount: r.ApproximateRowCount,
                ColumnCount: r.ColumnCount,
                HasPrimaryKey: r.HasPrimaryKey,
                OutgoingForeignKeys: r.OutgoingFkCount,
                IncomingForeignKeys: r.IncomingFkCount,
                IndexCount: r.IndexCount,
                TotalSizeBytes: r.TotalSizeBytes,
                TotalSizeHuman: FormatBytes(r.TotalSizeBytes),
                Comment: r.Comment))
            .ToList();

        return new SchemaInfo(
            Name: group.Key,
            TableCount: objects.Count(o => o.Kind == DatabaseObjectKind.Table),
            ViewCount: objects.Count(o => o.Kind == DatabaseObjectKind.View),
            Objects: objects
        );
    }

    private static string BuildSummary(
        int tables, int views, int schemaCount, long approxRows, long totalBytes)
    {
        var objectDesc = views > 0
            ? $"{tables} table{(tables != 1 ? "s" : "")} and {views} view{(views != 1 ? "s" : "")}"
            : $"{tables} table{(tables != 1 ? "s" : "")} and 0 views";
        var schemaDesc = $"{schemaCount} schema{(schemaCount != 1 ? "s" : "")}";
        return $"Found {objectDesc} across {schemaDesc}. Total approximate rows: {approxRows}. Total size on disk: {FormatBytes(totalBytes)}.";
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024L => $"{bytes} B",
        < 1024L * 1024L => (bytes / 1024.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024L * 1024L => (bytes / (1024.0 * 1024.0)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " GB",
    };

    private sealed record RawRow(
        string SchemaName,
        string ObjectName,
        DatabaseObjectKind ObjectKind,
        long? ApproximateRowCount,
        long ColumnCount,
        bool HasPrimaryKey,
        long OutgoingFkCount,
        long IncomingFkCount,
        long IndexCount,
        long TotalSizeBytes,
        string? Comment
    );
}
