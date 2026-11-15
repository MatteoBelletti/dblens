using System.Data.Common;
using System.Text.Json;
using DbLens.Core.Analysis;
using DbLens.Core.Inspection;
using DbLens.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace DbLens.SqlServer;

public sealed partial class SqlServerInspector(string connectionString, ILogger<SqlServerInspector> logger)
    : DatabaseInspectorBase(logger)
{
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
            CAST(ep.value AS NVARCHAR(MAX))     AS object_comment
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
            ic.is_descending_key,
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

    public override DatabaseDialect Dialect => DatabaseDialect.SqlServer;

    protected override string DialectDisplayName => "SQL Server";

    protected override string DefaultSchema => "dbo";

    protected override DbConnection CreateConnection() => new SqlConnection(connectionString);

    protected override IReadOnlyList<string> ServerHosts =>
        [new SqlConnectionStringBuilder(connectionString).DataSource];

    protected override Task<List<OverviewRow>> QueryOverviewAsync(
        DbConnection conn, SchemaOverviewRequest request, CancellationToken ct)
    {
        var cmd = Command(conn, SchemaOverviewSql);
        cmd.Parameters.Add(new SqlParameter("schema_filter", System.Data.SqlDbType.NVarChar, 128)
            { Value = (object?)request.SchemaFilter ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("name_pattern", System.Data.SqlDbType.NVarChar, 256)
            { Value = (object?)request.NamePattern ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("include_views", System.Data.SqlDbType.Bit) { Value = request.IncludeViews });
        cmd.Parameters.Add(new SqlParameter("include_system", System.Data.SqlDbType.Bit) { Value = request.IncludeSystemSchemas });

        return ReadAllAsync(cmd, r => new OverviewRow(
            SchemaName: r.Text("schema_name"),
            ObjectName: r.Text("object_name"),
            ObjectKind: r.Text("object_type") == "U" ? DatabaseObjectKind.Table : DatabaseObjectKind.View,
            ApproximateRowCount: r.NumberOrNull("approximate_row_count"),
            ColumnCount: r.Number("column_count"),
            HasPrimaryKey: r.Flag("has_primary_key"),
            OutgoingFkCount: r.Number("outgoing_fk_count"),
            IncomingFkCount: r.Number("incoming_fk_count"),
            IndexCount: r.Number("index_count"),
            TotalSizeBytes: r.Number("total_size_bytes"),
            Comment: r.TextOrNull("object_comment")), ct);
    }

    protected override async Task<TableCatalog> ReadTableCatalogAsync(
        DbConnection conn, IReadOnlyList<ParsedName> names, CancellationToken ct)
    {
        DbCommand For(string sql) => ScopedCommand(conn, sql, names.Select(n => (n.Schema, n.Name)));

        var found = await ReadAllAsync(For(TableDetailTablesSql), r => (
            Key: Key(r),
            Requested: (r.Text("requested_schema"), r.Text("requested_name")),
            Kind: r.Text("object_type") == "V" ? "view" : "table",
            Comment: r.TextOrNull("comment")), ct);

        var columns = await ReadAllAsync(For(TableDetailColumnsSql), r => (Key: Key(r), Value: new ColumnDetail(
            Name: r.Text("column_name"),
            OrdinalPosition: (int)r.Number("ordinal_position"),
            DataType: r.Text("data_type"),
            FullDataType: r.Text("full_data_type"),
            IsNullable: r.Flag("is_nullable"),
            DefaultExpression: r.TextOrNull("default_expression"),
            CharacterMaxLength: (int?)r.NumberOrNull("character_max_length"),
            NumericPrecision: (int?)r.NumberOrNull("numeric_precision"),
            NumericScale: (int?)r.NumberOrNull("numeric_scale"),
            IsIdentity: r.Flag("is_identity"),
            Comment: r.TextOrNull("column_comment"),
            IsGenerated: r.Flag("is_generated"))), ct);

        // One row per index column.
        var indexColumns = await ReadAllAsync(For(TableDetailIndexesSql), r => (
            Key: Key(r),
            Index: r.Text("index_name"),
            IsUnique: r.Flag("is_unique"),
            IsPrimary: r.Flag("is_primary_key"),
            Method: r.Text("type_desc"),
            HasFilter: r.Flag("has_filter"),
            Filter: r.TextOrNull("filter_definition"),
            Position: r.Number("index_column_id"),
            KeyOrdinal: r.Number("key_ordinal"),
            IsIncluded: r.Flag("is_included_column"),
            IsDescending: r.Flag("is_descending_key"),
            Column: r.Text("column_name")), ct);

        var indexes = indexColumns
            .GroupBy(x => (x.Key, x.Index))
            .Select(g =>
            {
                var first = g.First();
                var key = g.Where(x => x.KeyOrdinal > 0).OrderBy(x => x.KeyOrdinal).Select(x => x.Column).ToArray();
                var included = g.Where(x => x.IsIncluded).OrderBy(x => x.Position).Select(x => x.Column).ToArray();
                var descending = g.Where(x => x.KeyOrdinal > 0 && x.IsDescending).Select(x => x.Column).ToArray();

                return (first.Key, Value: new IndexDetail(
                    Name: first.Index,
                    Columns: key,
                    IsUnique: first.IsUnique || first.IsPrimary,
                    IsPrimary: first.IsPrimary,
                    Method: first.Method,
                    Definition: IndexDefinition(first.Key, first.Index, first.IsUnique || first.IsPrimary, first.Method,
                        key, descending, included, first.Filter),
                    // The filter's text needs VIEW DEFINITION; that there is one does not.
                    IsPartial: first.HasFilter,
                    Predicate: first.Filter,
                    IncludedColumns: included.Length == 0 ? null : included,
                    DescendingColumns: descending.Length == 0 ? null : descending));
            })
            // A columnstore index has no key: it orders nothing, so the index rules have no use for it.
            .Where(x => x.Value.Columns.Count > 0);

        // One row per constraint column. Names are unique per schema only, hence the id.
        var foreignKeys = (await ReadAllAsync(For(TableDetailForeignKeysSql), r => (
                Id: r.Number("constraint_id"),
                Position: r.Number("constraint_column_id"),
                FromColumn: r.Text("from_column"),
                ToColumn: r.Text("to_column"),
                Key: new ForeignKeyDetail(
                    r.Text("constraint_name"), r.Text("from_schema"), r.Text("from_table"), [],
                    r.Text("to_schema"), r.Text("to_table"), [], r.Text("on_delete"), r.Text("on_update"))), ct))
            .GroupBy(x => x.Id)
            .Select(g => g.First().Key with
            {
                FromColumns = [.. g.OrderBy(x => x.Position).Select(x => x.FromColumn)],
                ToColumns = [.. g.OrderBy(x => x.Position).Select(x => x.ToColumn)],
            })
            .ToList();

        var checks = await ReadAllAsync(For(TableDetailCheckConstraintsSql), r => (Key: Key(r),
            Value: new CheckConstraintDetail(r.Text("constraint_name"), r.Text("expression"))), ct);

        var uniques = (await ReadAllAsync(For(TableDetailUniqueConstraintsSql), r => (
                Key: Key(r),
                Constraint: r.Text("constraint_name"),
                Position: r.Number("index_column_id"),
                Column: r.Text("column_name")), ct))
            .GroupBy(x => (x.Key, x.Constraint))
            .Select(g => (g.Key.Key, Value: new UniqueConstraintDetail(
                g.Key.Constraint, [.. g.OrderBy(x => x.Position).Select(x => x.Column)])));

        return new TableCatalog(
            found.ToDictionary(t => t.Requested, t => t.Key),
            found.DistinctBy(t => t.Key).ToDictionary(t => t.Key, t => (t.Kind, t.Comment)),
            columns.ToLookup(x => x.Key, x => x.Value),
            indexes.ToLookup(x => x.Key, x => x.Value),
            foreignKeys,
            checks.ToLookup(x => x.Key, x => x.Value),
            uniques.ToLookup(x => x.Key, x => x.Value));
    }

    private static (string Schema, string Name) Key(DbDataReader r) => (r.Text("schema_name"), r.Text("table_name"));

    /// <summary>A command over the tables given as @pairs, a JSON array of {s, n} read with OPENJSON.</summary>
    private static DbCommand ScopedCommand(DbConnection conn, string sql, IEnumerable<(string Schema, string Name)> tables)
    {
        var pairs = JsonSerializer.Serialize(tables.Distinct().Select(t => new { s = t.Schema, n = t.Name }));
        var cmd = Command(conn, sql);
        cmd.Parameters.Add(new SqlParameter("pairs", System.Data.SqlDbType.NVarChar, -1) { Value = pairs });
        return cmd;
    }

    private static string IndexDefinition(
        (string Schema, string Name) table, string index, bool unique, string method,
        string[] columns, string[] descending, string[] included, string? filter)
    {
        static string Quote(string name) => SqlIdentifier.Quote(name, DatabaseDialect.SqlServer);

        return "CREATE " + (unique ? "UNIQUE " : "") +
               (method.Equals("NONCLUSTERED", StringComparison.OrdinalIgnoreCase) ? "" : method + " ") +
               $"INDEX {Quote(index)} ON {Quote(table.Schema)}.{Quote(table.Name)} " +
               $"({string.Join(", ", columns.Select(c => Quote(c) + (descending.Contains(c) ? " DESC" : "")))})" +
               (included.Length > 0 ? $" INCLUDE ({string.Join(", ", included.Select(Quote))})" : "") +
               (filter is null ? "" : $" WHERE {filter}");
    }
}
