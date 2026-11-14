using System.Diagnostics;
using System.Data.Common;
using DbLens.Core.Inspection;
using DbLens.Core.Models;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector : DatabaseInspectorBase
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresInspector> _logger;

    // relkind: r table, p partitioned table, v view, m materialized view.
    //
    // A partitioned table stores nothing itself, so its rows and size are those of its leaf
    // partitions. A foreign key that involves one is cloned onto every partition: conparentid = 0
    // keeps the declared constraint and drops the clones.
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
            CASE
                WHEN c.relkind = 'p' THEN (
                    SELECT CASE WHEN bool_or(l.reltuples < 0) THEN NULL ELSE sum(l.reltuples)::bigint END
                    FROM pg_partition_tree(c.oid) t
                    JOIN pg_class l ON l.oid = t.relid
                    WHERE t.isleaf)
                WHEN c.reltuples < 0 THEN NULL
                ELSE c.reltuples::bigint
            END                                         AS approximate_row_count,
            (SELECT count(*) FROM pg_attribute a
                WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            )                                           AS column_count,
            EXISTS (SELECT 1 FROM pg_constraint pk
                WHERE pk.conrelid = c.oid AND pk.contype = 'p'
            )                                           AS has_primary_key,
            (SELECT count(*) FROM pg_constraint fk
                WHERE fk.conrelid = c.oid AND fk.contype = 'f' AND fk.conparentid = 0
            )                                           AS outgoing_fk_count,
            (SELECT count(*) FROM pg_constraint fk
                WHERE fk.confrelid = c.oid AND fk.contype = 'f' AND fk.conparentid = 0
            )                                           AS incoming_fk_count,
            (SELECT count(*) FROM pg_index i
                WHERE i.indrelid = c.oid
            )                                           AS index_count,
            CASE WHEN c.relkind = 'p' THEN (
                    SELECT COALESCE(sum(pg_total_relation_size(t.relid)), 0)::bigint
                    FROM pg_partition_tree(c.oid) t
                    WHERE t.isleaf)
                 ELSE pg_total_relation_size(c.oid)
            END                                         AS total_size_bytes,
            obj_description(c.oid, 'pg_class')          AS object_comment,
            (SELECT pn.nspname || '.' || pc.relname
                FROM pg_inherits i
                JOIN pg_class pc ON pc.oid = i.inhparent
                JOIN pg_namespace pn ON pn.oid = pc.relnamespace
                WHERE c.relispartition AND i.inhrelid = c.oid
                LIMIT 1
            )                                           AS partition_of,
            current_database()                          AS database_name
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        CROSS JOIN params p
        WHERE
            c.relkind IN ('r', 'p', 'v', 'm')
            AND (p.include_views OR c.relkind IN ('r', 'p'))
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
        WHERE c.relkind IN ('r', 'p', 'v', 'm')
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
            a.attgenerated <> ''                             AS is_generated,
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
        AND c.relkind IN ('r', 'p', 'v', 'm')
        AND (n.nspname, c.relname) IN (
            SELECT * FROM unnest(@schemas::text[], @names::text[])
        )
        ORDER BY n.nspname, c.relname, a.attnum
        """;

    // Key columns are plain column names (expressions keep their text), so they compare equal to
    // the names foreign keys and constraints report; pg_get_indexdef would quote "Mixed Case".
    // INCLUDE columns are carried, not ordered by, and are reported apart from the key.
    private const string TableDetailIndexesSql = """
        WITH index_cols AS (
            SELECT
                ix.indexrelid,
                array_agg(COALESCE(a.attname::text, pg_get_indexdef(ix.indexrelid, k.n, true)) ORDER BY k.n)
                    FILTER (WHERE k.n <= ix.indnkeyatts) AS columns,
                COALESCE(array_agg(a.attname::text ORDER BY k.n)
                    FILTER (WHERE k.n > ix.indnkeyatts), '{}'::text[]) AS included_columns
            FROM pg_index ix
            CROSS JOIN LATERAL generate_series(1, ix.indnatts) AS k(n)
            LEFT JOIN pg_attribute a
                ON a.attrelid = ix.indrelid AND a.attnum = ix.indkey[k.n - 1] AND ix.indkey[k.n - 1] <> 0
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
            ic.columns,
            ic.included_columns
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
            array_agg(a_from.attname::text ORDER BY kord.n) AS from_columns,
            array_agg(a_to.attname::text   ORDER BY kord.n) AS to_columns,
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
        AND con.conparentid = 0
        AND (
            (n_from.nspname, ct_from.relname) IN (SELECT * FROM unnest(@schemas::text[], @names::text[]))
            OR
            (n_to.nspname,   ct_to.relname)   IN (SELECT * FROM unnest(@schemas::text[], @names::text[]))
        )
        GROUP BY con.oid, n_from.nspname, ct_from.relname, n_to.nspname, ct_to.relname,
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
            array_agg(a.attname::text ORDER BY kord.n)       AS columns
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
        : base(logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public override DatabaseDialect Dialect => DatabaseDialect.PostgreSql;

    protected override string DialectDisplayName => "PostgreSQL";

    protected override string DefaultSchema => "public";

    protected override DbConnection CreateConnection() => new NpgsqlConnection(_connectionString);

    // Host may list several servers for failover; an unset Host means the local default socket or localhost.
    protected override IReadOnlyList<string> ServerHosts =>
        (new NpgsqlConnectionStringBuilder(_connectionString).Host ?? "localhost")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public override async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
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
            "Opening PostgreSQL connection for schema overview (schema={Schema}, pattern={Pattern})",
            request.SchemaFilter ?? "*", request.NamePattern ?? "*");

        var sw = Stopwatch.StartNew();

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = SchemaOverviewSql;
            cmd.CommandTimeout = QueryTimeoutSeconds;

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

            // A partition's rows and bytes are already in its partitioned table's figures.
            var counted = schemas.SelectMany(s => s.Objects).Where(o => o.PartitionOf is null).ToList();

            var totalTables = schemas.Sum(s => s.TableCount);
            var totalViews = schemas.Sum(s => s.ViewCount);
            var totalBytes = counted.Sum(o => o.TotalSizeBytes);
            var approxRows = counted
                .Where(o => o.ApproximateRowCount.HasValue)
                .Sum(o => o.ApproximateRowCount!.Value);
            var anyUnanalyzed = counted.Any(o => o.Kind == DatabaseObjectKind.Table && o.ApproximateRowCount == null);
            var partitions = rows.Count(r => r.PartitionOf is not null);

            sw.Stop();
            _logger.LogInformation(
                "Schema overview completed in {ElapsedMs}ms: {Tables} tables, {Views} views across {SchemaCount} schema(s)",
                sw.ElapsedMilliseconds, totalTables, totalViews, schemas.Count);

            return new DatabaseSchemaOverview(
                Summary: BuildSummary(totalTables, totalViews, schemas.Count, approxRows, totalBytes, anyUnanalyzed, partitions),
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
    }

    protected override async Task<TableDetailsResult> GetTableDetailsAsync(
        IReadOnlyList<ParsedName> parsed,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Opening PostgreSQL connection for table details ({Count} names)", parsed.Count);

        var sw = Stopwatch.StartNew();

        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            var schemas = parsed.Select(p => p.Schema).ToArray();
            var names = parsed.Select(p => p.Name).ToArray();
            string? databaseName = null;

            var tableInfo = new Dictionary<(string, string), (string Kind, string? Comment)>();
            await using (var cmd = BuildDetailCmd(conn, TableDetailTablesSql, schemas, names))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    databaseName ??= r.GetString(r.GetOrdinal("database_name"));
                    var kind = r.GetChar(r.GetOrdinal("object_kind")) switch
                    {
                        'v' => "view",
                        'm' => "materialized view",
                        _ => "table",
                    };
                    var comment = r.IsDBNull(r.GetOrdinal("comment")) ? null : r.GetString(r.GetOrdinal("comment"));
                    tableInfo[TableKey(r)] = (kind, comment);
                }
            }

            var columnsByTable = new Dictionary<(string, string), List<ColumnDetail>>();
            await using (var cmd = BuildDetailCmd(conn, TableDetailColumnsSql, schemas, names))
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

            var indexesByTable = new Dictionary<(string, string), List<IndexDetail>>();
            await using (var cmd = BuildDetailCmd(conn, TableDetailIndexesSql, schemas, names))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    var included = r.GetFieldValue<string[]>(r.GetOrdinal("included_columns"));

                    ListFor(indexesByTable, TableKey(r)).Add(new IndexDetail(
                        Name: r.GetString(r.GetOrdinal("index_name")),
                        Columns: r.GetFieldValue<string[]>(r.GetOrdinal("columns")),
                        IsUnique: r.GetBoolean(r.GetOrdinal("is_unique")),
                        IsPrimary: r.GetBoolean(r.GetOrdinal("is_primary")),
                        Method: r.GetString(r.GetOrdinal("method")),
                        Definition: r.GetString(r.GetOrdinal("definition")),
                        IsPartial: r.GetBoolean(r.GetOrdinal("is_partial")),
                        Predicate: r.IsDBNull(r.GetOrdinal("predicate"))
                            ? null : r.GetString(r.GetOrdinal("predicate")),
                        IncludedColumns: included.Length == 0 ? null : included));
                }
            }

            // Outgoing and incoming.
            var foreignKeys = new List<ForeignKeyDetail>();
            await using (var cmd = BuildDetailCmd(conn, TableDetailForeignKeysSql, schemas, names))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                {
                    foreignKeys.Add(new ForeignKeyDetail(
                        ConstraintName: r.GetString(r.GetOrdinal("constraint_name")),
                        FromSchema: r.GetString(r.GetOrdinal("from_schema")),
                        FromTable: r.GetString(r.GetOrdinal("from_table")),
                        FromColumns: r.GetFieldValue<string[]>(r.GetOrdinal("from_columns")),
                        ToSchema: r.GetString(r.GetOrdinal("to_schema")),
                        ToTable: r.GetString(r.GetOrdinal("to_table")),
                        ToColumns: r.GetFieldValue<string[]>(r.GetOrdinal("to_columns")),
                        OnDelete: r.GetString(r.GetOrdinal("on_delete")),
                        OnUpdate: r.GetString(r.GetOrdinal("on_update"))));
                }
            }

            var checksByTable = new Dictionary<(string, string), List<CheckConstraintDetail>>();
            await using (var cmd = BuildDetailCmd(conn, TableDetailCheckConstraintsSql, schemas, names))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                    ListFor(checksByTable, TableKey(r)).Add(new CheckConstraintDetail(
                        Name: r.GetString(r.GetOrdinal("constraint_name")),
                        Expression: r.GetString(r.GetOrdinal("expression"))));
            }

            // pg_constraint contype = 'u', distinct from standalone UNIQUE indexes.
            var uniquesByTable = new Dictionary<(string, string), List<UniqueConstraintDetail>>();
            await using (var cmd = BuildDetailCmd(conn, TableDetailUniqueConstraintsSql, schemas, names))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
            {
                while (await r.ReadAsync(ct))
                    ListFor(uniquesByTable, TableKey(r)).Add(new UniqueConstraintDetail(
                        Name: r.GetString(r.GetOrdinal("constraint_name")),
                        Columns: r.GetFieldValue<string[]>(r.GetOrdinal("columns"))));
            }

            // PostgreSQL compares names exactly, so a requested name is its own catalog name.
            var result = BuildTableDetails(parsed, new TableCatalog(
                databaseName ?? conn.Database,
                tableInfo.Keys.ToDictionary(k => k, k => k),
                tableInfo, columnsByTable, indexesByTable, foreignKeys, checksByTable, uniquesByTable));

            _logger.LogInformation(
                "Table details completed in {ElapsedMs}ms: {Found} found, {NotFound} not found",
                sw.ElapsedMilliseconds, result.Tables.Count, result.NotFound.Count);

            return result;
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
    }

    // --- helpers ---

    private static (string, string) TableKey(NpgsqlDataReader r) =>
        (r.GetString(r.GetOrdinal("schema_name")), r.GetString(r.GetOrdinal("table_name")));

    private static List<T> ListFor<T>(Dictionary<(string, string), List<T>> byTable, (string, string) key)
    {
        if (!byTable.TryGetValue(key, out var list))
            byTable[key] = list = [];
        return list;
    }

    private static NpgsqlCommand BuildDetailCmd(NpgsqlConnection conn, string sql, string[] schemas, string[] names)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        cmd.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = schemas });
        cmd.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = names });
        return cmd;
    }

    private static OverviewRow ReadRow(NpgsqlDataReader r) => new(
        SchemaName: r.GetString(r.GetOrdinal("schema_name")),
        ObjectName: r.GetString(r.GetOrdinal("object_name")),
        ObjectKind: r.GetChar(r.GetOrdinal("object_kind")) is 'v' or 'm'
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
            : r.GetString(r.GetOrdinal("object_comment")),
        PartitionOf: r.IsDBNull(r.GetOrdinal("partition_of"))
            ? null
            : r.GetString(r.GetOrdinal("partition_of"))
    );

    private static string BuildSummary(
        int tables, int views, int schemaCount, long approxRows, long totalBytes, bool anyUnanalyzed, int partitions)
    {
        var objectDesc = views > 0
            ? $"{tables} table{(tables != 1 ? "s" : "")} and {views} view{(views != 1 ? "s" : "")}"
            : $"{tables} table{(tables != 1 ? "s" : "")} and 0 views";
        var schemaDesc = $"{schemaCount} schema{(schemaCount != 1 ? "s" : "")}";
        var rowNote = anyUnanalyzed
            ? $"Total approximate rows: {approxRows} (some tables not yet analyzed; run ANALYZE for accurate counts)."
            : $"Total approximate rows: {approxRows}.";
        var partitionNote = partitions > 0
            ? $" {partitions} of the tables are partitions (PartitionOf set): their rows and size are counted once, in their partitioned table."
            : "";
        return $"Found {objectDesc} across {schemaDesc}. {rowNote} Total size on disk: {FormatBytes(totalBytes)}.{partitionNote} " +
               "NOTE: column names are NOT included in this response; only ColumnCount per object is available. " +
               "Do NOT infer or generate column names. Call get_table_details for exact column information.";
    }
}
