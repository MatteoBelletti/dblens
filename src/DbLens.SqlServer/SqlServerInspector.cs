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
    private const string TableDetailTablesSql = """
        SELECT
            s.name                              AS schema_name,
            o.name                              AS table_name,
            RTRIM(o.type)                       AS object_type,
            CAST(ep.value AS NVARCHAR(MAX))     AS comment,
            DB_NAME()                           AS database_name
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN sys.extended_properties ep
            ON ep.major_id = o.object_id
           AND ep.minor_id = 0
           AND ep.class    = 1
           AND ep.name     = 'MS_Description'
        WHERE RTRIM(o.type) IN ('U', 'V')
        AND EXISTS (
            SELECT 1 FROM OPENJSON(@pairs)
            WITH (s NVARCHAR(128) '$.s', n NVARCHAR(128) '$.n') AS p
            WHERE p.s = s.name AND p.n = o.name
        )
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
            CAST(ep.value AS NVARCHAR(MAX))                     AS column_comment
        FROM sys.columns c
        JOIN sys.tables t ON t.object_id = c.object_id
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
            i.filter_definition,
            ic.index_column_id,
            c.name                  AS column_name
        FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.index_columns ic
            ON ic.object_id = i.object_id AND ic.index_id = i.index_id
           AND ic.is_included_column = 0
        JOIN sys.columns c
            ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.type > 0
        AND i.is_unique_constraint = 0
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
            ic.index_column_id,
            c.name              AS column_name
        FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.index_columns ic
            ON ic.object_id = i.object_id AND ic.index_id = i.index_id
           AND ic.is_included_column = 0
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

    public override async Task<TableDetailsResult> GetTableDetailsAsync(
        TableDetailsRequest request,
        CancellationToken ct = default)
    {
        var parsed = ParseTableNames(request.Tables, request.DefaultSchema);

        _logger.LogInformation(
            "Opening SQL Server connection for table details ({Count} tables)", parsed.Length);

        var sw = Stopwatch.StartNew();

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Serialize the (schema, name) pairs as JSON for OPENJSON usage in all queries
            var pairs = JsonSerializer.Serialize(
                parsed.Select(p => new { s = p.Schema, n = p.Name }));

            string? databaseName = null;

            // 1. Tables
            var tableInfo = new Dictionary<(string, string), (string Kind, string? Comment)>();
            {
                var sw1 = Stopwatch.StartNew();
                await using var cmd = BuildDetailCmd(conn, TableDetailTablesSql, pairs);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    databaseName ??= r.GetString(r.GetOrdinal("database_name"));
                    var sn = r.GetString(r.GetOrdinal("schema_name"));
                    var tn = r.GetString(r.GetOrdinal("table_name"));
                    var kind = r.GetString(r.GetOrdinal("object_type")) == "V" ? "view" : "table";
                    var comment = r.IsDBNull(r.GetOrdinal("comment")) ? null : r.GetString(r.GetOrdinal("comment"));
                    tableInfo[(sn, tn)] = (kind, comment);
                }
                _logger.LogDebug("Table details — tables query: {ElapsedMs}ms", sw1.ElapsedMilliseconds);
            }

            // 2. Columns
            var columnsByTable = new Dictionary<(string, string), List<ColumnDetail>>();
            {
                var sw2 = Stopwatch.StartNew();
                await using var cmd = BuildDetailCmd(conn, TableDetailColumnsSql, pairs);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var key = (r.GetString(r.GetOrdinal("schema_name")), r.GetString(r.GetOrdinal("table_name")));
                    if (!columnsByTable.TryGetValue(key, out var list))
                        columnsByTable[key] = list = [];

                    list.Add(new ColumnDetail(
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
                            ? null : r.GetString(r.GetOrdinal("column_comment"))
                    ));
                }
                _logger.LogDebug("Table details — columns query: {ElapsedMs}ms", sw2.ElapsedMilliseconds);
            }

            // 3. Indexes (raw rows, grouped in memory)
            var indexesByTable = new Dictionary<(string, string), List<IndexDetail>>();
            {
                var sw3 = Stopwatch.StartNew();
                await using var cmd = BuildDetailCmd(conn, TableDetailIndexesSql, pairs);
                await using var r = await cmd.ExecuteReaderAsync(ct);

                // Read all raw index rows, then group
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
                        FilterDefinition: r.IsDBNull(r.GetOrdinal("filter_definition"))
                            ? null : r.GetString(r.GetOrdinal("filter_definition")),
                        IndexColumnId: r.GetInt32(r.GetOrdinal("index_column_id")),
                        ColumnName: r.GetString(r.GetOrdinal("column_name"))
                    ));
                }

                foreach (var grp in rawIndexRows.GroupBy(x => (x.SchemaName, x.TableName, x.IndexName)))
                {
                    var first = grp.First();
                    var key = (first.SchemaName, first.TableName);
                    if (!indexesByTable.TryGetValue(key, out var list))
                        indexesByTable[key] = list = [];

                    var cols = grp.OrderBy(x => x.IndexColumnId).Select(x => x.ColumnName).ToArray();
                    list.Add(new IndexDetail(
                        Name: first.IndexName,
                        Columns: cols,
                        IsUnique: first.IsUnique || first.IsPrimary,
                        IsPrimary: first.IsPrimary,
                        Method: first.TypeDesc,
                        Definition: BuildSqlServerIndexDef(
                            first.IsUnique, first.IsPrimary, first.TypeDesc,
                            first.SchemaName, first.TableName, first.IndexName,
                            cols, first.FilterDefinition),
                        IsPartial: first.FilterDefinition is not null,
                        Predicate: first.FilterDefinition
                    ));
                }
                _logger.LogDebug("Table details — indexes query: {ElapsedMs}ms", sw3.ElapsedMilliseconds);
            }

            // 4. Foreign keys (raw rows, grouped by constraint)
            var allFks = new List<FkRow>();
            {
                var sw4 = Stopwatch.StartNew();
                await using var cmd = BuildDetailCmd(conn, TableDetailForeignKeysSql, pairs);
                await using var r = await cmd.ExecuteReaderAsync(ct);

                var rawFkRows = new List<RawFkRow>();
                while (await r.ReadAsync(ct))
                {
                    rawFkRows.Add(new RawFkRow(
                        FromSchema: r.GetString(r.GetOrdinal("from_schema")),
                        FromTable: r.GetString(r.GetOrdinal("from_table")),
                        ToSchema: r.GetString(r.GetOrdinal("to_schema")),
                        ToTable: r.GetString(r.GetOrdinal("to_table")),
                        ConstraintName: r.GetString(r.GetOrdinal("constraint_name")),
                        ConstraintColumnId: r.GetInt32(r.GetOrdinal("constraint_column_id")),
                        FromColumn: r.GetString(r.GetOrdinal("from_column")),
                        ToColumn: r.GetString(r.GetOrdinal("to_column")),
                        OnDelete: r.GetString(r.GetOrdinal("on_delete")),
                        OnUpdate: r.GetString(r.GetOrdinal("on_update"))
                    ));
                }

                foreach (var grp in rawFkRows.GroupBy(x => x.ConstraintName))
                {
                    var first = grp.First();
                    var ordered = grp.OrderBy(x => x.ConstraintColumnId).ToList();
                    allFks.Add(new FkRow(
                        FromSchema: first.FromSchema,
                        FromTable: first.FromTable,
                        ToSchema: first.ToSchema,
                        ToTable: first.ToTable,
                        ConstraintName: first.ConstraintName,
                        FromColumns: ordered.Select(x => x.FromColumn).ToArray(),
                        ToColumns: ordered.Select(x => x.ToColumn).ToArray(),
                        OnDelete: first.OnDelete,
                        OnUpdate: first.OnUpdate
                    ));
                }
                _logger.LogDebug("Table details — foreign keys query: {ElapsedMs}ms", sw4.ElapsedMilliseconds);
            }

            // 5. Check constraints
            var checksByTable = new Dictionary<(string, string), List<CheckConstraintDetail>>();
            {
                var sw5 = Stopwatch.StartNew();
                await using var cmd = BuildDetailCmd(conn, TableDetailCheckConstraintsSql, pairs);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var key = (r.GetString(r.GetOrdinal("schema_name")), r.GetString(r.GetOrdinal("table_name")));
                    if (!checksByTable.TryGetValue(key, out var list))
                        checksByTable[key] = list = [];

                    list.Add(new CheckConstraintDetail(
                        Name: r.GetString(r.GetOrdinal("constraint_name")),
                        Expression: r.GetString(r.GetOrdinal("expression"))
                    ));
                }
                _logger.LogDebug("Table details — check constraints query: {ElapsedMs}ms", sw5.ElapsedMilliseconds);
            }

            // 6. Unique constraints (is_unique_constraint = 1, reported separately from indexes)
            var uniquesByTable = new Dictionary<(string, string), List<UniqueConstraintDetail>>();
            {
                var sw6 = Stopwatch.StartNew();
                await using var cmd = BuildDetailCmd(conn, TableDetailUniqueConstraintsSql, pairs);
                await using var r = await cmd.ExecuteReaderAsync(ct);

                var rawUqRows = new List<RawUqRow>();
                while (await r.ReadAsync(ct))
                {
                    rawUqRows.Add(new RawUqRow(
                        SchemaName: r.GetString(r.GetOrdinal("schema_name")),
                        TableName: r.GetString(r.GetOrdinal("table_name")),
                        ConstraintName: r.GetString(r.GetOrdinal("constraint_name")),
                        IndexColumnId: r.GetInt32(r.GetOrdinal("index_column_id")),
                        ColumnName: r.GetString(r.GetOrdinal("column_name"))
                    ));
                }

                foreach (var grp in rawUqRows.GroupBy(x => (x.SchemaName, x.TableName, x.ConstraintName)))
                {
                    var first = grp.First();
                    var key = (first.SchemaName, first.TableName);
                    if (!uniquesByTable.TryGetValue(key, out var list))
                        uniquesByTable[key] = list = [];

                    list.Add(new UniqueConstraintDetail(
                        Name: first.ConstraintName,
                        Columns: grp.OrderBy(x => x.IndexColumnId).Select(x => x.ColumnName).ToArray()
                    ));
                }
                _logger.LogDebug("Table details — unique constraints query: {ElapsedMs}ms", sw6.ElapsedMilliseconds);
            }

            // Assemble result preserving input order
            var notFound = new List<string>();
            var tables = new List<TableDetail>();

            foreach (var p in parsed)
            {
                var key = (p.Schema, p.Name);
                if (!tableInfo.TryGetValue(key, out var info))
                {
                    notFound.Add(p.Original);
                    continue;
                }

                var idxs = indexesByTable.GetValueOrDefault(key, [])
                    .OrderBy(i => i.Name, StringComparer.Ordinal)
                    .ToList();

                var pkIdx = idxs.FirstOrDefault(i => i.IsPrimary);

                var fkOut = allFks
                    .Where(fk => fk.FromSchema == p.Schema && fk.FromTable == p.Name)
                    .OrderBy(fk => fk.ConstraintName, StringComparer.Ordinal)
                    .Select(ToFkDetail)
                    .ToList();

                var fkIn = allFks
                    .Where(fk => fk.ToSchema == p.Schema && fk.ToTable == p.Name)
                    .OrderBy(fk => fk.ConstraintName, StringComparer.Ordinal)
                    .Select(ToFkDetail)
                    .ToList();

                tables.Add(new TableDetail(
                    Schema: p.Schema,
                    Name: p.Name,
                    Kind: info.Kind,
                    Comment: info.Comment,
                    Columns: columnsByTable.GetValueOrDefault(key, []),
                    PrimaryKey: pkIdx is null ? null : new PrimaryKeyInfo(pkIdx.Name, pkIdx.Columns),
                    Indexes: idxs,
                    ForeignKeysOutgoing: fkOut,
                    ForeignKeysIncoming: fkIn,
                    CheckConstraints: checksByTable.GetValueOrDefault(key, [])
                        .OrderBy(c => c.Name, StringComparer.Ordinal).ToList(),
                    UniqueConstraints: uniquesByTable.GetValueOrDefault(key, [])
                        .OrderBy(u => u.Name, StringComparer.Ordinal).ToList()
                ));
            }

            var totalColumns = tables.Sum(t => t.Columns.Count);
            var totalIndexes = tables.Sum(t => t.Indexes.Count);
            var totalFks = allFks.Select(fk => fk.ConstraintName).Distinct(StringComparer.Ordinal).Count();

            sw.Stop();
            _logger.LogInformation(
                "Table details completed in {ElapsedMs}ms: {Found} found, {NotFound} not found",
                sw.ElapsedMilliseconds, tables.Count, notFound.Count);

            var found = tables.Count;
            var requested = request.Tables.Length;
            return new TableDetailsResult(
                Summary:
                    $"Retrieved details for {found} of {requested} requested table{(requested != 1 ? "s" : "")}. " +
                    $"Found {totalColumns} column{(totalColumns != 1 ? "s" : "")}, " +
                    $"{totalIndexes} index{(totalIndexes != 1 ? "es" : "")}, " +
                    $"{totalFks} foreign key{(totalFks != 1 ? "s" : "")}. " +
                    "NOTE: view SQL bodies (SELECT definitions) are NOT included — do not infer or generate view logic. " +
                    "Row data is NOT included — do not infer or generate sample values. " +
                    "Tables in the NotFound list do not exist in this database — do not infer or generate their structure.",
                Dialect: "SqlServer",
                Database: databaseName ?? "unknown",
                GeneratedAt: DateTime.UtcNow.ToString("O"),
                Truncated: false,
                NotFound: notFound,
                Tables: tables
            );
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

    private static ForeignKeyDetail ToFkDetail(FkRow fk) => new(
        ConstraintName: fk.ConstraintName,
        FromSchema: fk.FromSchema,
        FromTable: fk.FromTable,
        FromColumns: fk.FromColumns,
        ToSchema: fk.ToSchema,
        ToTable: fk.ToTable,
        ToColumns: fk.ToColumns,
        OnDelete: fk.OnDelete,
        OnUpdate: fk.OnUpdate
    );

    private static string BuildSqlServerIndexDef(
        bool isUnique, bool isPrimary, string typeDesc,
        string schema, string table, string indexName,
        string[] columns, string? filterDefinition)
    {
        var parts = new System.Text.StringBuilder("CREATE ");
        if (isUnique || isPrimary) parts.Append("UNIQUE ");
        if (!string.Equals(typeDesc, "NONCLUSTERED", StringComparison.OrdinalIgnoreCase))
            parts.Append(typeDesc).Append(' ');
        parts.Append("INDEX [").Append(indexName).Append("] ON [")
            .Append(schema).Append("].[").Append(table).Append("] (")
            .Append(string.Join(", ", columns.Select(c => $"[{c}]")))
            .Append(')');
        if (filterDefinition is not null)
            parts.Append(" WHERE ").Append(filterDefinition);
        return parts.ToString();
    }

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

    private sealed record FkRow(
        string FromSchema, string FromTable,
        string ToSchema, string ToTable,
        string ConstraintName,
        string[] FromColumns, string[] ToColumns,
        string OnDelete, string OnUpdate
    );

    private sealed record RawIndexRow(
        string SchemaName, string TableName, string IndexName,
        bool IsUnique, bool IsPrimary, string TypeDesc,
        string? FilterDefinition, int IndexColumnId, string ColumnName
    );

    private sealed record RawFkRow(
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
