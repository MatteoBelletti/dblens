using System.Diagnostics;
using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace DbLens.Postgres;

public sealed class PostgresInspector : IDatabaseInspector
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresInspector> _logger;

    private const string SchemaOverviewSql = """
        WITH params AS (
            SELECT
                @schema_filter::text AS schema_filter,
                @name_pattern::text  AS name_pattern,
                @include_views::bool AS include_views,
                @include_system::bool AS include_system
        )
        SELECT
            n.nspname                                   AS schema_name,
            c.relname                                   AS object_name,
            c.relkind                                   AS object_kind,
            CASE WHEN c.reltuples < 0 THEN NULL
                 ELSE c.reltuples::bigint
            END                                         AS approximate_row_count,
            (SELECT count(*) FROM pg_attribute a
                WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            )                                           AS column_count,
            EXISTS (SELECT 1 FROM pg_constraint pk
                WHERE pk.conrelid = c.oid AND pk.contype = 'p'
            )                                           AS has_primary_key,
            (SELECT count(*) FROM pg_constraint fk
                WHERE fk.conrelid = c.oid AND fk.contype = 'f'
            )                                           AS outgoing_fk_count,
            (SELECT count(*) FROM pg_constraint fk
                WHERE fk.confrelid = c.oid AND fk.contype = 'f'
            )                                           AS incoming_fk_count,
            (SELECT count(*) FROM pg_index i
                WHERE i.indrelid = c.oid
            )                                           AS index_count,
            pg_total_relation_size(c.oid)               AS total_size_bytes,
            obj_description(c.oid, 'pg_class')          AS object_comment,
            current_database()                          AS database_name
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        CROSS JOIN params p
        WHERE
            c.relkind IN ('r', 'v')
            AND (p.include_views OR c.relkind = 'r')
            AND (p.include_system
                 OR (n.nspname NOT LIKE 'pg\_%' ESCAPE '\'
                     AND n.nspname <> 'information_schema'))
            AND (p.schema_filter IS NULL OR n.nspname = p.schema_filter)
            AND (p.name_pattern IS NULL OR c.relname LIKE p.name_pattern)
        ORDER BY n.nspname, c.relname;
        """;

    public PostgresInspector(string connectionString, ILogger<PostgresInspector> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public DatabaseDialect Dialect => DatabaseDialect.PostgreSql;

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
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
            "Opening PostgreSQL connection for schema overview (schema={Schema}, pattern={Pattern})",
            request.SchemaFilter ?? "*", request.NamePattern ?? "*");

        var sw = Stopwatch.StartNew();

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = SchemaOverviewSql;

            cmd.Parameters.Add(new NpgsqlParameter("schema_filter", NpgsqlDbType.Text)
                { Value = (object?)request.SchemaFilter ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("name_pattern", NpgsqlDbType.Text)
                { Value = (object?)request.NamePattern ?? DBNull.Value });
            cmd.Parameters.Add(new NpgsqlParameter("include_views", NpgsqlDbType.Boolean)
                { Value = request.IncludeViews });
            cmd.Parameters.Add(new NpgsqlParameter("include_system", NpgsqlDbType.Boolean)
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
            var anyUnanalyzed = schemas.SelectMany(s => s.Objects).Any(o => o.ApproximateRowCount == null);

            sw.Stop();
            _logger.LogInformation(
                "Schema overview completed in {ElapsedMs}ms: {Tables} tables, {Views} views across {SchemaCount} schema(s)",
                sw.ElapsedMilliseconds, totalTables, totalViews, schemas.Count);

            return new DatabaseSchemaOverview(
                Summary: BuildSummary(totalTables, totalViews, schemas.Count, approxRows, totalBytes, anyUnanalyzed),
                Dialect: "PostgreSql",
                Database: databaseName ?? "unknown",
                GeneratedAt: DateTime.UtcNow.ToString("O"),
                Schemas: schemas
            );
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NpgsqlException ex)
        {
            throw new DatabaseInspectionException(
                $"PostgreSQL error during schema overview: {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            throw new DatabaseInspectionException(
                $"Timeout during schema overview: {ex.Message}", ex);
        }
    }

    private static RawRow ReadRow(NpgsqlDataReader r) => new(
        SchemaName: r.GetString(r.GetOrdinal("schema_name")),
        ObjectName: r.GetString(r.GetOrdinal("object_name")),
        ObjectKind: r.GetChar(r.GetOrdinal("object_kind")) == 'v'
            ? DatabaseObjectKind.View
            : DatabaseObjectKind.Table,
        ApproximateRowCount: r.IsDBNull(r.GetOrdinal("approximate_row_count"))
            ? null
            : r.GetInt64(r.GetOrdinal("approximate_row_count")),
        ColumnCount: r.GetInt64(r.GetOrdinal("column_count")),
        HasPrimaryKey: r.GetBoolean(r.GetOrdinal("has_primary_key")),
        OutgoingFkCount: r.GetInt64(r.GetOrdinal("outgoing_fk_count")),
        IncomingFkCount: r.GetInt64(r.GetOrdinal("incoming_fk_count")),
        IndexCount: r.GetInt64(r.GetOrdinal("index_count")),
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
        int tables, int views, int schemaCount, long approxRows, long totalBytes, bool anyUnanalyzed)
    {
        var objectDesc = views > 0
            ? $"{tables} table{(tables != 1 ? "s" : "")} and {views} view{(views != 1 ? "s" : "")}"
            : $"{tables} table{(tables != 1 ? "s" : "")} and 0 views";
        var schemaDesc = $"{schemaCount} schema{(schemaCount != 1 ? "s" : "")}";
        var rowNote = anyUnanalyzed
            ? $"Total approximate rows: {approxRows} (some tables not yet analyzed; run ANALYZE for accurate counts)."
            : $"Total approximate rows: {approxRows}.";
        return $"Found {objectDesc} across {schemaDesc}. {rowNote} Total size on disk: {FormatBytes(totalBytes)}.";
    }

    internal static string FormatBytes(long bytes) => bytes switch
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
