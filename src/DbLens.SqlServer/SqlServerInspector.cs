using System.Diagnostics;
using System.Text.Json;
using System.Data.Common;
using DbLens.Core.Inspection;
using DbLens.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DbLens.SqlServer;

public sealed partial class SqlServerInspector : DatabaseInspectorBase
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

    // For table details, SQL Server uses OPENJSON to accept a JSON array of {s, n} objects.
    // Names are compared by the database's collation, so on a case-insensitive database "USERS"
    // finds "users": the requested pair is returned beside the real one to map between them.
    private const string TableDetailTablesSql = """
        SELECT
            s.name                              AS schema_name,
            o.name                              AS table_name,
            RTRIM(o.type)                       AS object_type,
            CAST(ep.value AS NVARCHAR(MAX))     AS comment,
            DB_NAME()                           AS database_name,
            p.s                                 AS requested_schema,
            p.n                                 AS requested_name
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN OPENJSON(@pairs)
            WITH (s NVARCHAR(128) '$.s', n NVARCHAR(128) '$.n') AS p
            ON p.s = s.name AND p.n = o.name
        LEFT JOIN sys.extended_properties ep
            ON ep.major_id = o.object_id
           AND ep.minor_id = 0
           AND ep.class    = 1
           AND ep.name     = 'MS_Description'
        WHERE RTRIM(o.type) IN ('U', 'V')
        ORDER BY s.name, o.name
        """;

    private const string TableDetailColumnsSql = """
        SELECT
            s.name                                              AS schema_name,
            t.name                                              AS table_name,
            c.name                                              AS column_name,
            c.column_id                                         AS ordinal_position,
            ty.name                                             AS data_type,
            CASE
                WHEN ty.name IN ('nvarchar','nchar') AND c.max_length = -1
                    THEN ty.name + '(MAX)'
                WHEN ty.name IN ('nvarchar','nchar')
                    THEN ty.name + '(' + CAST(c.max_length / 2 AS NVARCHAR) + ')'
                WHEN ty.name IN ('varchar','char','binary','varbinary') AND c.max_length = -1
                    THEN ty.name + '(MAX)'
                WHEN ty.name IN ('varchar','char','binary','varbinary')
                    THEN ty.name + '(' + CAST(c.max_length AS NVARCHAR) + ')'
                WHEN ty.name IN ('decimal','numeric')
                    THEN ty.name + '(' + CAST(c.precision AS NVARCHAR) + ', ' + CAST(c.scale AS NVARCHAR) + ')'
                WHEN ty.name IN ('datetime2','time','datetimeoffset') AND c.scale < 7
                    THEN ty.name + '(' + CAST(c.scale AS NVARCHAR) + ')'
                ELSE ty.name
            END                                                 AS full_data_type,
            c.is_nullable,
            dc.definition                                       AS default_expression,
            CASE
                WHEN ty.name IN ('nvarchar','nchar') AND c.max_length != -1
                    THEN c.max_length / 2
                WHEN ty.name IN ('varchar','char','binary','varbinary') AND c.max_length != -1
                    THEN CAST(c.max_length AS INT)
                ELSE NULL
            END                                                 AS character_max_length,
            CASE
                WHEN ty.name IN ('decimal','numeric','float','real',
                                  'int','bigint','smallint','tinyint',
                                  'money','smallmoney')
                    THEN CAST(c.precision AS INT)
                ELSE NULL
            END                                                 AS numeric_precision,
            CASE
                WHEN ty.name IN ('decimal','numeric','money','smallmoney')
                    THEN CAST(c.scale AS INT)
                WHEN ty.name IN ('int','bigint','smallint','tinyint','float','real')
                    THEN 0
                ELSE NULL
            END                                                 AS numeric_scale,
            c.is_identity,
            CAST(CASE WHEN c.is_computed = 1 OR c.generated_always_type <> 0 THEN 1 ELSE 0 END AS BIT)
                                                                AS is_generated,
            CAST(ep.value AS NVARCHAR(MAX))                     AS column_comment
        FROM sys.columns c
        JOIN sys.objects t ON t.object_id = c.object_id AND RTRIM(t.type) IN ('U', 'V')
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        LEFT JOIN sys.default_constraints dc
            ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
        LEFT JOIN sys.extended_properties ep
            ON ep.major_id  = c.object_id
           AND ep.minor_id  = c.column_id
           AND ep.class     = 1
           AND ep.name      = 'MS_Description'
        WHERE EXISTS (
            SELECT 1 FROM OPENJSON(@pairs)
            WITH (s NVARCHAR(128) '$.s', n NVARCHAR(128) '$.n') AS p
            WHERE p.s = s.name AND p.n = t.name
        )
        ORDER BY s.name, t.name, c.column_id
        """;

    private const string TableDetailIndexesSql = """
        SELECT
            s.name                  AS schema_name,
            t.name                  AS table_name,
            i.name                  AS index_name,
            i.is_unique,
            i.is_primary_key,
            i.type_desc,
            i.has_filter,
            i.filter_definition,
            ic.index_column_id,
            ic.key_ordinal,
            ic.is_included_column,
            c.name                  AS column_name
        FROM sys.indexes i
        JOIN sys.objects t ON t.object_id = i.object_id AND RTRIM(t.type) IN ('U', 'V')
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.index_columns ic
            ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.type > 0
        AND i.is_unique_constraint = 0
        AND i.is_hypothetical = 0
        AND i.is_disabled = 0
        AND EXISTS (
            SELECT 1 FROM OPENJSON(@pairs)
            WITH (s NVARCHAR(128) '$.s', n NVARCHAR(128) '$.n') AS p
            WHERE p.s = s.name AND p.n = t.name
        )
        ORDER BY s.name, t.name, i.name, ic.index_column_id
        """;

    private const string TableDetailForeignKeysSql = """
        SELECT
            fs.name                                 AS from_schema,
            ft.name                                 AS from_table,
            rs.name                                 AS to_schema,
            rt.name                                 AS to_table,
            fk.name                                 AS constraint_name,
            fk.object_id                            AS constraint_id,
            fkc.constraint_column_id,
            pc.name                                 AS from_column,
            rc.name                                 AS to_column,
            REPLACE(fk.delete_referential_action_desc, '_', ' ') AS on_delete,
            REPLACE(fk.update_referential_action_desc, '_', ' ') AS on_update
        FROM sys.foreign_keys fk
        JOIN sys.tables ft ON ft.object_id = fk.parent_object_id
        JOIN sys.schemas fs ON fs.schema_id = ft.schema_id
        JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
        JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns pc
            ON pc.object_id = fk.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns rc
            ON rc.object_id = fk.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE EXISTS (
            SELECT 1 FROM OPENJSON(@pairs)
            WITH (s NVARCHAR(128) '$.s', n NVARCHAR(128) '$.n') AS p
            WHERE (p.s = fs.name AND p.n = ft.name)
               OR (p.s = rs.name AND p.n = rt.name)
        )
        ORDER BY fk.name, fkc.constraint_column_id
        """;

    private const string TableDetailCheckConstraintsSql = """
        SELECT
            s.name      AS schema_name,
            t.name      AS table_name,
            cc.name     AS constraint_name,
            cc.definition AS expression
        FROM sys.check_constraints cc
        JOIN sys.tables t ON t.object_id = cc.parent_object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE EXISTS (
            SELECT 1 FROM OPENJSON(@pairs)
            WITH (s NVARCHAR(128) '$.s', n NVARCHAR(128) '$.n') AS p
            WHERE p.s = s.name AND p.n = t.name
        )
        ORDER BY s.name, t.name, cc.name
        """;

    private const string TableDetailUniqueConstraintsSql = """
        SELECT
            s.name              AS schema_name,
            t.name              AS table_name,
            i.name              AS constraint_name,
            ic.key_ordinal      AS index_column_id,
            c.name              AS column_name
        FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.index_columns ic
            ON ic.object_id = i.object_id AND ic.index_id = i.index_id
           AND ic.key_ordinal > 0
        JOIN sys.columns c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.is_unique_constraint = 1
        AND EXISTS (
            SELECT 1 FROM OPENJSON(@pairs)
            WITH (s NVARCHAR(128) '$.s', n NVARCHAR(128) '$.n') AS p
            WHERE p.s = s.name AND p.n = t.name
        )
        ORDER BY s.name, t.name, i.name, ic.index_column_id
        """;


    public SqlServerInspector(string connectionString, ILogger<SqlServerInspector> logger)
        : base(logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public override DatabaseDialect Dialect => DatabaseDialect.SqlServer;

    protected override string DialectDisplayName => "SQL Server";

    protected override string DefaultSchema => "dbo";

    protected override DbConnection CreateConnection() => new SqlConnection(_connectionString);

    protected override IReadOnlyList<string> ServerHosts =>
        [new SqlConnectionStringBuilder(_connectionString).DataSource];

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct);
        return true;
    }

    public override async Task<DatabaseSchemaOverview> GetSchemaOverviewAsync(
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
            cmd.CommandTimeout = QueryTimeoutSeconds;

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

            var rows = new List<OverviewRow>();
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
    }

    protected override async Task<TableDetailsResult> GetTableDetailsAsync(
        IReadOnlyList<ParsedName> parsed,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Opening SQL Server connection for table details ({Count} names)", parsed.Count);

        var sw = Stopwatch.StartNew();

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Serialize the (schema, name) pairs as JSON for OPENJSON usage in all queries
            var pairs = JsonSerializer.Serialize(
                parsed.Select(p => new { s = p.Schema, n = p.Name }).Distinct());

            string? databaseName = null;

            var actualNames = new Dictionary<(string, string), (string, string)>();
            var tableInfo = new Dictionary<(string, string), (string Kind, string? Comment)>();
            await using (var cmd = BuildDetailCmd(conn, TableDetailTablesSql, pairs))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    databaseName ??= r.GetString(r.GetOrdinal("database_name"));
                    var key = TableKey(r);
                    var kind = r.GetString(r.GetOrdinal("object_type")) == "V" ? "view" : "table";
                    var comment = r.IsDBNull(r.GetOrdinal("comment")) ? null : r.GetString(r.GetOrdinal("comment"));
                    tableInfo[key] = (kind, comment);
                    actualNames[(r.GetString(r.GetOrdinal("requested_schema")), r.GetString(r.GetOrdinal("requested_name")))] = key;
                }
            }

            var columnsByTable = new Dictionary<(string, string), List<ColumnDetail>>();
            await using (var cmd = BuildDetailCmd(conn, TableDetailColumnsSql, pairs))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    ListFor(columnsByTable, TableKey(r)).Add(new ColumnDetail(
                        Name: r.GetString(r.GetOrdinal("column_name")),
                        OrdinalPosition: r.GetInt32(r.GetOrdinal("ordinal_position")),
                        DataType: r.GetString(r.GetOrdinal("data_type")),
                        FullDataType: r.GetString(r.GetOrdinal("full_data_type")),
                        IsNullable: r.GetBoolean(r.GetOrdinal("is_nullable")),
                        DefaultExpression: r.IsDBNull(r.GetOrdinal("default_expression"))
                            ? null : r.GetString(r.GetOrdinal("default_expression")),
                        CharacterMaxLength: r.IsDBNull(r.GetOrdinal("character_max_length"))
                            ? null : r.GetInt32(r.GetOrdinal("character_max_length")),
                        NumericPrecision: r.IsDBNull(r.GetOrdinal("numeric_precision"))
                            ? null : r.GetInt32(r.GetOrdinal("numeric_precision")),
                        NumericScale: r.IsDBNull(r.GetOrdinal("numeric_scale"))
                            ? null : r.GetInt32(r.GetOrdinal("numeric_scale")),
                        IsIdentity: r.GetBoolean(r.GetOrdinal("is_identity")),
                        Comment: r.IsDBNull(r.GetOrdinal("column_comment"))
                            ? null : r.GetString(r.GetOrdinal("column_comment")),
                        IsGenerated: r.GetBoolean(r.GetOrdinal("is_generated"))));
                }
            }

            // One row per index column, grouped in memory.
            var indexesByTable = new Dictionary<(string, string), List<IndexDetail>>();
            await using (var cmd = BuildDetailCmd(conn, TableDetailIndexesSql, pairs))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                var rawIndexRows = new List<RawIndexRow>();
                while (await r.ReadAsync(ct))
                {
                    rawIndexRows.Add(new RawIndexRow(
                        SchemaName: r.GetString(r.GetOrdinal("schema_name")),
                        TableName: r.GetString(r.GetOrdinal("table_name")),
                        IndexName: r.GetString(r.GetOrdinal("index_name")),
                        IsUnique: r.GetBoolean(r.GetOrdinal("is_unique")),
                        IsPrimary: r.GetBoolean(r.GetOrdinal("is_primary_key")),
                        TypeDesc: r.GetString(r.GetOrdinal("type_desc")),
                        HasFilter: r.GetBoolean(r.GetOrdinal("has_filter")),
                        FilterDefinition: r.IsDBNull(r.GetOrdinal("filter_definition"))
                            ? null : r.GetString(r.GetOrdinal("filter_definition")),
                        IndexColumnId: r.GetInt32(r.GetOrdinal("index_column_id")),
                        KeyOrdinal: Convert.ToInt32(r.GetValue(r.GetOrdinal("key_ordinal"))),
                        IsIncluded: r.GetBoolean(r.GetOrdinal("is_included_column")),
                        ColumnName: r.GetString(r.GetOrdinal("column_name"))));
                }

                foreach (var grp in rawIndexRows.GroupBy(x => (x.SchemaName, x.TableName, x.IndexName)))
                {
                    var first = grp.First();
                    var cols = grp.Where(x => x.KeyOrdinal > 0).OrderBy(x => x.KeyOrdinal).Select(x => x.ColumnName).ToArray();
                    var included = grp.Where(x => x.IsIncluded).OrderBy(x => x.IndexColumnId).Select(x => x.ColumnName).ToArray();

                    // A columnstore index has no key: it orders nothing, so the index rules have no use for it.
                    if (cols.Length == 0) continue;

                    ListFor(indexesByTable, (first.SchemaName, first.TableName)).Add(new IndexDetail(
                        Name: first.IndexName,
                        Columns: cols,
                        IsUnique: first.IsUnique || first.IsPrimary,
                        IsPrimary: first.IsPrimary,
                        Method: first.TypeDesc,
                        Definition: BuildSqlServerIndexDef(
                            first.IsUnique, first.IsPrimary, first.TypeDesc,
                            first.SchemaName, first.TableName, first.IndexName,
                            cols, included, first.FilterDefinition),
                        // The filter's text needs VIEW DEFINITION; that there is one does not.
                        IsPartial: first.HasFilter,
                        Predicate: first.FilterDefinition,
                        IncludedColumns: included.Length == 0 ? null : included));
                }
            }

            // Outgoing and incoming; one row per constraint column.
            var foreignKeys = new List<ForeignKeyDetail>();
            await using (var cmd = BuildDetailCmd(conn, TableDetailForeignKeysSql, pairs))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                var rawFkRows = new List<RawFkRow>();
                while (await r.ReadAsync(ct))
                {
                    rawFkRows.Add(new RawFkRow(
                        ConstraintId: r.GetInt32(r.GetOrdinal("constraint_id")),
                        FromSchema: r.GetString(r.GetOrdinal("from_schema")),
                        FromTable: r.GetString(r.GetOrdinal("from_table")),
                        ToSchema: r.GetString(r.GetOrdinal("to_schema")),
                        ToTable: r.GetString(r.GetOrdinal("to_table")),
                        ConstraintName: r.GetString(r.GetOrdinal("constraint_name")),
                        ConstraintColumnId: r.GetInt32(r.GetOrdinal("constraint_column_id")),
                        FromColumn: r.GetString(r.GetOrdinal("from_column")),
                        ToColumn: r.GetString(r.GetOrdinal("to_column")),
                        OnDelete: r.GetString(r.GetOrdinal("on_delete")),
                        OnUpdate: r.GetString(r.GetOrdinal("on_update"))));
                }

                foreach (var grp in rawFkRows.GroupBy(x => x.ConstraintId))
                {
                    var first = grp.First();
                    var ordered = grp.OrderBy(x => x.ConstraintColumnId).ToList();
                    foreignKeys.Add(new ForeignKeyDetail(
                        ConstraintName: first.ConstraintName,
                        FromSchema: first.FromSchema,
                        FromTable: first.FromTable,
                        FromColumns: ordered.Select(x => x.FromColumn).ToArray(),
                        ToSchema: first.ToSchema,
                        ToTable: first.ToTable,
                        ToColumns: ordered.Select(x => x.ToColumn).ToArray(),
                        OnDelete: first.OnDelete,
                        OnUpdate: first.OnUpdate));
                }
            }

            var checksByTable = new Dictionary<(string, string), List<CheckConstraintDetail>>();
            await using (var cmd = BuildDetailCmd(conn, TableDetailCheckConstraintsSql, pairs))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                    ListFor(checksByTable, TableKey(r)).Add(new CheckConstraintDetail(
                        Name: r.GetString(r.GetOrdinal("constraint_name")),
                        Expression: r.GetString(r.GetOrdinal("expression"))));
            }

            var uniquesByTable = new Dictionary<(string, string), List<UniqueConstraintDetail>>();
            await using (var cmd = BuildDetailCmd(conn, TableDetailUniqueConstraintsSql, pairs))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                var rawUqRows = new List<RawUqRow>();
                while (await r.ReadAsync(ct))
                {
                    rawUqRows.Add(new RawUqRow(
                        SchemaName: r.GetString(r.GetOrdinal("schema_name")),
                        TableName: r.GetString(r.GetOrdinal("table_name")),
                        ConstraintName: r.GetString(r.GetOrdinal("constraint_name")),
                        IndexColumnId: Convert.ToInt32(r.GetValue(r.GetOrdinal("index_column_id"))),
                        ColumnName: r.GetString(r.GetOrdinal("column_name"))));
                }

                foreach (var grp in rawUqRows.GroupBy(x => (x.SchemaName, x.TableName, x.ConstraintName)))
                {
                    var first = grp.First();
                    ListFor(uniquesByTable, (first.SchemaName, first.TableName)).Add(new UniqueConstraintDetail(
                        Name: first.ConstraintName,
                        Columns: grp.OrderBy(x => x.IndexColumnId).Select(x => x.ColumnName).ToArray()));
                }
            }

            var result = BuildTableDetails(parsed, new TableCatalog(
                databaseName ?? conn.Database, actualNames, tableInfo, columnsByTable, indexesByTable, foreignKeys,
                checksByTable, uniquesByTable));

            _logger.LogInformation(
                "Table details completed in {ElapsedMs}ms: {Found} found, {NotFound} not found",
                sw.ElapsedMilliseconds, result.Tables.Count, result.NotFound.Count);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SqlException ex)
        {
            throw new DatabaseInspectionException(
                $"SQL Server error during table details: {ex.Message}", ex);
        }
    }

    // --- helpers ---

    private static SqlCommand BuildDetailCmd(SqlConnection conn, string sql, string pairs)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        cmd.Parameters.Add(new SqlParameter("pairs", System.Data.SqlDbType.NVarChar, -1) { Value = pairs });
        return cmd;
    }

    private static (string, string) TableKey(SqlDataReader r) =>
        (r.GetString(r.GetOrdinal("schema_name")), r.GetString(r.GetOrdinal("table_name")));

    private static List<T> ListFor<T>(Dictionary<(string, string), List<T>> byTable, (string, string) key)
    {
        if (!byTable.TryGetValue(key, out var list))
            byTable[key] = list = [];
        return list;
    }

    private static string BuildSqlServerIndexDef(
        bool isUnique, bool isPrimary, string typeDesc,
        string schema, string table, string indexName,
        string[] columns, string[] included, string? filterDefinition)
    {
        var parts = new System.Text.StringBuilder("CREATE ");
        if (isUnique || isPrimary) parts.Append("UNIQUE ");
        if (!string.Equals(typeDesc, "NONCLUSTERED", StringComparison.OrdinalIgnoreCase))
            parts.Append(typeDesc).Append(' ');
        parts.Append("INDEX ").Append(Quote(indexName)).Append(" ON ")
            .Append(Quote(schema)).Append('.').Append(Quote(table)).Append(" (")
            .Append(string.Join(", ", columns.Select(Quote)))
            .Append(')');
        if (included.Length > 0)
            parts.Append(" INCLUDE (").Append(string.Join(", ", included.Select(Quote))).Append(')');
        if (filterDefinition is not null)
            parts.Append(" WHERE ").Append(filterDefinition);
        return parts.ToString();
    }

    private static string Quote(string name) =>
        DbLens.Core.Analysis.SqlIdentifier.Quote(name, DatabaseDialect.SqlServer);

    private static OverviewRow ReadRow(SqlDataReader r) => new(
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

    private static string BuildSummary(
        int tables, int views, int schemaCount, long approxRows, long totalBytes)
    {
        var objectDesc = views > 0
            ? $"{tables} table{(tables != 1 ? "s" : "")} and {views} view{(views != 1 ? "s" : "")}"
            : $"{tables} table{(tables != 1 ? "s" : "")} and 0 views";
        var schemaDesc = $"{schemaCount} schema{(schemaCount != 1 ? "s" : "")}";
        return $"Found {objectDesc} across {schemaDesc}. Total approximate rows: {approxRows}. Total size on disk: {FormatBytes(totalBytes)}. " +
               "NOTE: column names are NOT included in this response; only ColumnCount per object is available. " +
               "Do NOT infer or generate column names. Call get_table_details for exact column information.";
    }

    private sealed record RawIndexRow(
        string SchemaName, string TableName, string IndexName,
        bool IsUnique, bool IsPrimary, string TypeDesc, bool HasFilter,
        string? FilterDefinition, int IndexColumnId, int KeyOrdinal, bool IsIncluded, string ColumnName
    );

    private sealed record RawFkRow(
        int ConstraintId,
        string FromSchema, string FromTable,
        string ToSchema, string ToTable,
        string ConstraintName, int ConstraintColumnId,
        string FromColumn, string ToColumn,
        string OnDelete, string OnUpdate
    );

    private sealed record RawUqRow(
        string SchemaName, string TableName,
        string ConstraintName, int IndexColumnId, string ColumnName
    );

}
