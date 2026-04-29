using System.Diagnostics;
using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector : IDatabaseInspector
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

    private const string TableDetailTablesSql = """
        SELECT
            n.nspname                          AS schema_name,
            c.relname                          AS table_name,
            c.relkind                          AS object_kind,
            obj_description(c.oid, 'pg_class') AS comment,
            current_database()                 AS database_name
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE c.relkind IN ('r', 'v')
        AND (n.nspname, c.relname) IN (
            SELECT * FROM unnest(@schemas::text[], @names::text[])
        )
        ORDER BY n.nspname, c.relname
        """;

    private const string TableDetailColumnsSql = """
        SELECT
            n.nspname                                        AS schema_name,
            c.relname                                        AS table_name,
            a.attname                                        AS column_name,
            a.attnum::int                                    AS ordinal_position,
            format_type(a.atttypid, a.atttypmod)             AS full_data_type,
            format_type(a.atttypid, -1)                      AS data_type,
            NOT a.attnotnull                                 AS is_nullable,
            pg_get_expr(d.adbin, d.adrelid)                  AS default_expression,
            ic.character_maximum_length::int                 AS character_max_length,
            ic.numeric_precision::int                        AS numeric_precision,
            ic.numeric_scale::int                            AS numeric_scale,
            (a.attidentity <> ''
             OR COALESCE(pg_get_expr(d.adbin, d.adrelid), '') LIKE 'nextval(%')
                                                             AS is_identity,
            col_description(a.attrelid, a.attnum)            AS column_comment
        FROM pg_attribute a
        JOIN pg_class c ON c.oid = a.attrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        LEFT JOIN information_schema.columns ic
            ON ic.table_schema = n.nspname
            AND ic.table_name = c.relname
            AND ic.column_name = a.attname
        WHERE a.attnum > 0
        AND NOT a.attisdropped
        AND (n.nspname, c.relname) IN (
            SELECT * FROM unnest(@schemas::text[], @names::text[])
        )
        ORDER BY n.nspname, c.relname, a.attnum
        """;

    private const string TableDetailIndexesSql = """
        WITH index_cols AS (
            SELECT
                ix.indexrelid,
                array_agg(
                    pg_get_indexdef(ix.indexrelid, k.n, true)
                    ORDER BY k.n
                ) AS columns
            FROM pg_index ix
            CROSS JOIN LATERAL generate_series(1, array_length(ix.indkey, 1)) AS k(n)
            GROUP BY ix.indexrelid
        )
        SELECT
            n.nspname                              AS schema_name,
            ct.relname                             AS table_name,
            ci.relname                             AS index_name,
            ix.indisunique                         AS is_unique,
            ix.indisprimary                        AS is_primary,
            am.amname                              AS method,
            pg_get_indexdef(ix.indexrelid)         AS definition,
            ix.indpred IS NOT NULL                 AS is_partial,
            pg_get_expr(ix.indpred, ix.indrelid)   AS predicate,
            ic.columns
        FROM pg_index ix
        JOIN pg_class ct ON ct.oid = ix.indrelid
        JOIN pg_class ci ON ci.oid = ix.indexrelid
        JOIN pg_namespace n ON n.oid = ct.relnamespace
        JOIN pg_am am ON am.oid = ci.relam
        JOIN index_cols ic ON ic.indexrelid = ix.indexrelid
        WHERE (n.nspname, ct.relname) IN (
            SELECT * FROM unnest(@schemas::text[], @names::text[])
        )
        ORDER BY n.nspname, ct.relname, ci.relname
        """;

    private const string TableDetailForeignKeysSql = """
        SELECT
            n_from.nspname                            AS from_schema,
            ct_from.relname                           AS from_table,
            n_to.nspname                              AS to_schema,
            ct_to.relname                             AS to_table,
            con.conname                               AS constraint_name,
            array_agg(a_from.attname ORDER BY kord.n) AS from_columns,
            array_agg(a_to.attname   ORDER BY kord.n) AS to_columns,
            CASE con.confdeltype
                WHEN 'a' THEN 'NO ACTION'
                WHEN 'r' THEN 'RESTRICT'
                WHEN 'c' THEN 'CASCADE'
                WHEN 'n' THEN 'SET NULL'
                WHEN 'd' THEN 'SET DEFAULT'
            END                                       AS on_delete,
            CASE con.confupdtype
                WHEN 'a' THEN 'NO ACTION'
                WHEN 'r' THEN 'RESTRICT'
                WHEN 'c' THEN 'CASCADE'
                WHEN 'n' THEN 'SET NULL'
                WHEN 'd' THEN 'SET DEFAULT'
            END                                       AS on_update
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
        AND (
            (n_from.nspname, ct_from.relname) IN (SELECT * FROM unnest(@schemas::text[], @names::text[]))
            OR
            (n_to.nspname,   ct_to.relname)   IN (SELECT * FROM unnest(@schemas::text[], @names::text[]))
        )
        GROUP BY n_from.nspname, ct_from.relname, n_to.nspname, ct_to.relname,
                 con.conname, con.confdeltype, con.confupdtype
        ORDER BY con.conname
        """;

    private const string TableDetailCheckConstraintsSql = """
        SELECT
            n.nspname                     AS schema_name,
            c.relname                     AS table_name,
            con.conname                   AS constraint_name,
            pg_get_constraintdef(con.oid) AS expression
        FROM pg_constraint con
        JOIN pg_class c ON c.oid = con.conrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE con.contype = 'c'
        AND (n.nspname, c.relname) IN (
            SELECT * FROM unnest(@schemas::text[], @names::text[])
        )
        ORDER BY n.nspname, c.relname, con.conname
        """;

    private const string TableDetailUniqueConstraintsSql = """
        SELECT
            n.nspname                                        AS schema_name,
            c.relname                                        AS table_name,
            con.conname                                      AS constraint_name,
            array_agg(a.attname ORDER BY kord.n)             AS columns
        FROM pg_constraint con
        JOIN pg_class c ON c.oid = con.conrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS kord(attnum, n)
        JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = kord.attnum
        WHERE con.contype = 'u'
        AND (n.nspname, c.relname) IN (
            SELECT * FROM unnest(@schemas::text[], @names::text[])
        )
        GROUP BY n.nspname, c.relname, con.conname
        ORDER BY n.nspname, c.relname, con.conname
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

    public async Task<TableDetailsResult> GetTableDetailsAsync(
        TableDetailsRequest request,
        CancellationToken ct = default)
    {
        var parsed = ParseTableNames(request.Tables, request.DefaultSchema ?? "public");

        _logger.LogInformation(
            "Opening PostgreSQL connection for table details ({Count} tables)", parsed.Length);

        var sw = Stopwatch.StartNew();

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var schemas = parsed.Select(p => p.Schema).ToArray();
            var names = parsed.Select(p => p.Name).ToArray();
            string? databaseName = null;

            // 1. Tables
            var tableInfo = new Dictionary<(string, string), (string Kind, string? Comment)>();
            {
                var sw1 = Stopwatch.StartNew();
                await using var cmd = BuildDetailCmd(conn, TableDetailTablesSql, schemas, names);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    databaseName ??= r.GetString(r.GetOrdinal("database_name"));
                    var sn = r.GetString(r.GetOrdinal("schema_name"));
                    var tn = r.GetString(r.GetOrdinal("table_name"));
                    var kind = r.GetChar(r.GetOrdinal("object_kind")) == 'v' ? "view" : "table";
                    var comment = r.IsDBNull(r.GetOrdinal("comment")) ? null : r.GetString(r.GetOrdinal("comment"));
                    tableInfo[(sn, tn)] = (kind, comment);
                }
                _logger.LogDebug("Table details — tables query: {ElapsedMs}ms", sw1.ElapsedMilliseconds);
            }

            // 2. Columns
            var columnsByTable = new Dictionary<(string, string), List<ColumnDetail>>();
            {
                var sw2 = Stopwatch.StartNew();
                await using var cmd = BuildDetailCmd(conn, TableDetailColumnsSql, schemas, names);
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

            // 3. Indexes
            var indexesByTable = new Dictionary<(string, string), List<IndexDetail>>();
            {
                var sw3 = Stopwatch.StartNew();
                await using var cmd = BuildDetailCmd(conn, TableDetailIndexesSql, schemas, names);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var key = (r.GetString(r.GetOrdinal("schema_name")), r.GetString(r.GetOrdinal("table_name")));
                    if (!indexesByTable.TryGetValue(key, out var list))
                        indexesByTable[key] = list = [];

                    list.Add(new IndexDetail(
                        Name: r.GetString(r.GetOrdinal("index_name")),
                        Columns: r.GetFieldValue<string[]>(r.GetOrdinal("columns")),
                        IsUnique: r.GetBoolean(r.GetOrdinal("is_unique")),
                        IsPrimary: r.GetBoolean(r.GetOrdinal("is_primary")),
                        Method: r.GetString(r.GetOrdinal("method")),
                        Definition: r.GetString(r.GetOrdinal("definition")),
                        IsPartial: r.GetBoolean(r.GetOrdinal("is_partial")),
                        Predicate: r.IsDBNull(r.GetOrdinal("predicate"))
                            ? null : r.GetString(r.GetOrdinal("predicate"))
                    ));
                }
                _logger.LogDebug("Table details — indexes query: {ElapsedMs}ms", sw3.ElapsedMilliseconds);
            }

            // 4. Foreign keys (outgoing + incoming)
            var allFks = new List<FkRow>();
            {
                var sw4 = Stopwatch.StartNew();
                await using var cmd = BuildDetailCmd(conn, TableDetailForeignKeysSql, schemas, names);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    allFks.Add(new FkRow(
                        FromSchema: r.GetString(r.GetOrdinal("from_schema")),
                        FromTable: r.GetString(r.GetOrdinal("from_table")),
                        ToSchema: r.GetString(r.GetOrdinal("to_schema")),
                        ToTable: r.GetString(r.GetOrdinal("to_table")),
                        ConstraintName: r.GetString(r.GetOrdinal("constraint_name")),
                        FromColumns: r.GetFieldValue<string[]>(r.GetOrdinal("from_columns")),
                        ToColumns: r.GetFieldValue<string[]>(r.GetOrdinal("to_columns")),
                        OnDelete: r.GetString(r.GetOrdinal("on_delete")),
                        OnUpdate: r.GetString(r.GetOrdinal("on_update"))
                    ));
                }
                _logger.LogDebug("Table details — foreign keys query: {ElapsedMs}ms", sw4.ElapsedMilliseconds);
            }

            // 5. Check constraints
            var checksByTable = new Dictionary<(string, string), List<CheckConstraintDetail>>();
            {
                var sw5 = Stopwatch.StartNew();
                await using var cmd = BuildDetailCmd(conn, TableDetailCheckConstraintsSql, schemas, names);
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

            // 6. Unique constraints (pg_constraint contype='u', distinct from standalone UNIQUE indexes)
            var uniquesByTable = new Dictionary<(string, string), List<UniqueConstraintDetail>>();
            {
                var sw6 = Stopwatch.StartNew();
                await using var cmd = BuildDetailCmd(conn, TableDetailUniqueConstraintsSql, schemas, names);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var key = (r.GetString(r.GetOrdinal("schema_name")), r.GetString(r.GetOrdinal("table_name")));
                    if (!uniquesByTable.TryGetValue(key, out var list))
                        uniquesByTable[key] = list = [];

                    list.Add(new UniqueConstraintDetail(
                        Name: r.GetString(r.GetOrdinal("constraint_name")),
                        Columns: r.GetFieldValue<string[]>(r.GetOrdinal("columns"))
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
                Dialect: "PostgreSql",
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
        catch (NpgsqlException ex)
        {
            throw new DatabaseInspectionException(
                $"PostgreSQL error during table details: {ex.Message}", ex);
        }
        catch (TimeoutException ex)
        {
            throw new DatabaseInspectionException(
                $"Timeout during table details: {ex.Message}", ex);
        }
    }

    // --- helpers ---

    private static NpgsqlCommand BuildDetailCmd(NpgsqlConnection conn, string sql, string[] schemas, string[] names)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas });
        cmd.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names });
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

    private static ParsedName[] ParseTableNames(string[] tables, string defaultSchema) =>
        tables.Select(t =>
        {
            var dot = t.IndexOf('.');
            return dot < 0
                ? new ParsedName(defaultSchema, t, t)
                : new ParsedName(t[..dot], t[(dot + 1)..], t);
        }).ToArray();

    // --- schema overview helpers (unchanged) ---

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
        return $"Found {objectDesc} across {schemaDesc}. {rowNote} Total size on disk: {FormatBytes(totalBytes)}. " +
               "NOTE: column names are NOT included in this response; only ColumnCount per object is available. " +
               "Do NOT infer or generate column names. Call get_table_details for exact column information.";
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024L => $"{bytes} B",
        < 1024L * 1024L => (bytes / 1024.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024L * 1024L => (bytes / (1024.0 * 1024.0)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + " GB",
    };

    private sealed record ParsedName(string Schema, string Name, string Original);

    private sealed record FkRow(
        string FromSchema, string FromTable,
        string ToSchema, string ToTable,
        string ConstraintName,
        string[] FromColumns, string[] ToColumns,
        string OnDelete, string OnUpdate
    );

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
