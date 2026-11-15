using System.Data.Common;
using DbLens.Core.Inspection;
using DbLens.Core.Models;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector(string connectionString, ILogger<PostgresInspector> logger)
    : DatabaseInspectorBase(logger)
{
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
            )                                           AS partition_of
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
            obj_description(c.oid, 'pg_class') AS comment
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
                    FILTER (WHERE k.n > ix.indnkeyatts), '{}'::text[]) AS included_columns,
                COALESCE(array_agg(COALESCE(a.attname::text, pg_get_indexdef(ix.indexrelid, k.n, true)) ORDER BY k.n)
                    FILTER (WHERE k.n <= ix.indnkeyatts AND (ix.indoption[k.n - 1] & 1) = 1), '{}'::text[])
                    AS descending_columns
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
            ic.included_columns,
            ic.descending_columns
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

    public override DatabaseDialect Dialect => DatabaseDialect.PostgreSql;

    protected override string DialectDisplayName => "PostgreSQL";

    protected override string DefaultSchema => "public";

    protected override DbConnection CreateConnection() => new NpgsqlConnection(connectionString);

    // Host may list several servers for failover; an unset Host means the local default socket or localhost.
    protected override IReadOnlyList<string> ServerHosts =>
        (new NpgsqlConnectionStringBuilder(connectionString).Host ?? "localhost")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    protected override Task<List<OverviewRow>> QueryOverviewAsync(
        DbConnection conn, SchemaOverviewRequest request, CancellationToken ct)
    {
        var cmd = Command(conn, SchemaOverviewSql);
        cmd.Parameters.Add(Text("schema_filter", request.SchemaFilter));
        cmd.Parameters.Add(Text("name_pattern", request.NamePattern));
        cmd.Parameters.Add(new NpgsqlParameter("include_views", NpgsqlDbType.Boolean) { Value = request.IncludeViews });
        cmd.Parameters.Add(new NpgsqlParameter("include_system", NpgsqlDbType.Boolean) { Value = request.IncludeSystemSchemas });

        return ReadAllAsync(cmd, r => new OverviewRow(
            SchemaName: r.Text("schema_name"),
            ObjectName: r.Text("object_name"),
            ObjectKind: r.Field<char>("object_kind") is 'v' or 'm' ? DatabaseObjectKind.View : DatabaseObjectKind.Table,
            ApproximateRowCount: r.NumberOrNull("approximate_row_count"),
            ColumnCount: r.Number("column_count"),
            HasPrimaryKey: r.Flag("has_primary_key"),
            OutgoingFkCount: r.Number("outgoing_fk_count"),
            IncomingFkCount: r.Number("incoming_fk_count"),
            IndexCount: r.Number("index_count"),
            TotalSizeBytes: r.Number("total_size_bytes"),
            Comment: r.TextOrNull("object_comment"),
            PartitionOf: r.TextOrNull("partition_of")), ct);
    }

    protected override async Task<TableCatalog> ReadTableCatalogAsync(
        DbConnection conn, IReadOnlyList<ParsedName> names, CancellationToken ct)
    {
        DbCommand For(string sql) => ScopedCommand(conn, sql, names.Select(n => (n.Schema, n.Name)));

        var tables = (await ReadAllAsync(For(TableDetailTablesSql), r => (
                Key: Key(r),
                Kind: r.Field<char>("object_kind") switch { 'v' => "view", 'm' => "materialized view", _ => "table" },
                Comment: r.TextOrNull("comment")), ct))
            .ToDictionary(t => t.Key, t => (t.Kind, t.Comment));

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

        var indexes = await ReadAllAsync(For(TableDetailIndexesSql), r => (Key: Key(r), Value: new IndexDetail(
            Name: r.Text("index_name"),
            Columns: r.Field<string[]>("columns"),
            IsUnique: r.Flag("is_unique"),
            IsPrimary: r.Flag("is_primary"),
            Method: r.Text("method"),
            Definition: r.Text("definition"),
            IsPartial: r.Flag("is_partial"),
            Predicate: r.TextOrNull("predicate"),
            IncludedColumns: r.Field<string[]>("included_columns") is { Length: > 0 } included ? included : null,
            DescendingColumns: r.Field<string[]>("descending_columns") is { Length: > 0 } descending ? descending : null)), ct);

        var foreignKeys = await ReadAllAsync(For(TableDetailForeignKeysSql), r => new ForeignKeyDetail(
            ConstraintName: r.Text("constraint_name"),
            FromSchema: r.Text("from_schema"),
            FromTable: r.Text("from_table"),
            FromColumns: r.Field<string[]>("from_columns"),
            ToSchema: r.Text("to_schema"),
            ToTable: r.Text("to_table"),
            ToColumns: r.Field<string[]>("to_columns"),
            OnDelete: r.Text("on_delete"),
            OnUpdate: r.Text("on_update")), ct);

        var checks = await ReadAllAsync(For(TableDetailCheckConstraintsSql), r => (Key: Key(r),
            Value: new CheckConstraintDetail(r.Text("constraint_name"), r.Text("expression"))), ct);

        // pg_constraint contype = 'u', distinct from standalone UNIQUE indexes.
        var uniques = await ReadAllAsync(For(TableDetailUniqueConstraintsSql), r => (Key: Key(r),
            Value: new UniqueConstraintDetail(r.Text("constraint_name"), r.Field<string[]>("columns"))), ct);

        // PostgreSQL compares names exactly, so a requested name is its own catalog name.
        return new TableCatalog(
            tables.Keys.ToDictionary(k => k, k => k),
            tables,
            columns.ToLookup(x => x.Key, x => x.Value),
            indexes.ToLookup(x => x.Key, x => x.Value),
            foreignKeys,
            checks.ToLookup(x => x.Key, x => x.Value),
            uniques.ToLookup(x => x.Key, x => x.Value));
    }

    private static (string Schema, string Name) Key(DbDataReader r) => (r.Text("schema_name"), r.Text("table_name"));

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    /// <summary>A command over the tables given as the parallel arrays @schemas and @names.</summary>
    private static DbCommand ScopedCommand(DbConnection conn, string sql, IEnumerable<(string Schema, string Name)> tables)
    {
        var scope = tables.ToList();
        var cmd = Command(conn, sql);
        cmd.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text)
            { Value = scope.Select(t => t.Schema).ToArray() });
        cmd.Parameters.Add(new NpgsqlParameter("names", NpgsqlDbType.Array | NpgsqlDbType.Text)
            { Value = scope.Select(t => t.Name).ToArray() });
        return cmd;
    }
}
