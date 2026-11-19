using System.Data.Common;
using System.Globalization;
using DbLens.Core.Abstractions;
using DbLens.Core.Analysis;
using DbLens.Core.Models;
using Microsoft.Extensions.Logging;

namespace DbLens.Core.Inspection;

/// <summary>
/// Everything an inspector does that does not depend on the SQL dialect: scope resolution, error
/// wrapping, caveats, the statement guard, the verification loop. A dialect supplies a connection
/// and catalog SQL. Built on System.Data.Common, so it needs no driver.
///
/// A rule enforced here cannot be forgotten by one dialect, and the two cannot drift apart.
/// </summary>
public abstract partial class DatabaseInspectorBase(ILogger logger) : IDatabaseInspector
{
    /// <summary>Applied to every command: Npgsql's own default is no timeout at all.</summary>
    protected const int QueryTimeoutSeconds = 30;

    protected ILogger Log { get; } = logger;

    public abstract DatabaseDialect Dialect { get; }

    /// <summary>Engine name for messages: "PostgreSQL" or "SQL Server".</summary>
    protected abstract string DialectDisplayName { get; }

    /// <summary>Schema of a table name without a prefix: "public" or "dbo".</summary>
    protected abstract string DefaultSchema { get; }

    /// <summary>A new, unopened connection. The caller owns and disposes it.</summary>
    protected abstract DbConnection CreateConnection();

    public string DatabaseName
    {
        get
        {
            using var conn = CreateConnection();
            return conn.Database;
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Command(conn, "SELECT 1");
        await cmd.ExecuteScalarAsync(ct);
        return true;
    }

    // ── Connections, commands, errors ──────────────────────────────────────────

    protected async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var conn = CreateConnection();
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    protected static DbCommand Command(DbConnection conn, string sql, DbTransaction? tx = null)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        cmd.Transaction = tx;
        return cmd;
    }

    /// <summary>A driver's error on one line, as a caveat quotes it.</summary>
    protected static string Brief(DbException ex) => ex.Message.ReplaceLineEndings(" ").Trim();

    protected static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>Maps every row of the command's result, then disposes the command.</summary>
    protected static async Task<List<T>> ReadAllAsync<T>(DbCommand cmd, Func<DbDataReader, T> map, CancellationToken ct)
    {
        await using (cmd)
        {
            List<T> rows = [];
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(map(reader));
            return rows;
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> in a transaction of its own that is configured by
    /// <paramref name="setupSql"/> and never committed. One per statement: an error poisons a
    /// PostgreSQL transaction for everything after it.
    /// </summary>
    protected static async Task<T> InRolledBackTransactionAsync<T>(
        DbConnection conn, string setupSql, CancellationToken ct, Func<DbTransaction, Task<T>> body)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var setup = Command(conn, setupSql, tx))
                await setup.ExecuteNonQueryAsync(ct);

            return await body(tx);
        }
        finally
        {
            await tx.RollbackAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Wraps driver errors in <see cref="DatabaseInspectionException"/>. Cancellation is not a
    /// DbException and passes through.
    /// </summary>
    protected async Task<T> RunAsync<T>(string operation, Func<Task<T>> body)
    {
        try
        {
            return await body();
        }
        catch (DbException ex)
        {
            throw new DatabaseInspectionException(
                $"{DialectDisplayName} error during {operation}: {ex.Message}", ex);
        }
    }

    // ── Schema overview ────────────────────────────────────────────────────────

    /// <summary>A row of the schema overview query, identical in shape on both dialects.</summary>
    protected sealed record OverviewRow(
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
        string? Comment,
        string? PartitionOf = null);

    protected abstract Task<List<OverviewRow>> QueryOverviewAsync(
        DbConnection conn, SchemaOverviewRequest request, CancellationToken ct);

    public Task<DatabaseSchemaOverview> GetSchemaOverviewAsync(
        SchemaOverviewRequest request, CancellationToken ct = default) =>
        RunAsync("schema overview", async () =>
        {
            await using var conn = await OpenAsync(ct);
            var rows = await QueryOverviewAsync(conn, request, ct);

            var schemas = rows
                .GroupBy(r => r.SchemaName)
                .OrderBy(g => g.Key)
                .Select(BuildSchemaInfo)
                .ToList();

            return new DatabaseSchemaOverview(
                Summary: BuildOverviewSummary(schemas),
                Dialect: Dialect.ToString(),
                Database: conn.Database,
                GeneratedAt: Now(),
                Schemas: schemas);
        });

    private static SchemaInfo BuildSchemaInfo(IGrouping<string, OverviewRow> group)
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
                Comment: r.Comment,
                PartitionOf: r.PartitionOf))
            .ToList();

        return new SchemaInfo(
            Name: group.Key,
            TableCount: objects.Count(o => o.Kind == DatabaseObjectKind.Table),
            ViewCount: objects.Count(o => o.Kind == DatabaseObjectKind.View),
            Objects: objects);
    }

    private static string BuildOverviewSummary(IReadOnlyList<SchemaInfo> schemas)
    {
        var objects = schemas.SelectMany(s => s.Objects).ToList();

        // A partition's rows and bytes are already in its partitioned table's figures.
        var counted = objects.Where(o => o.PartitionOf is null).ToList();
        var tables = schemas.Sum(s => s.TableCount);
        var views = schemas.Sum(s => s.ViewCount);
        var partitions = objects.Count - counted.Count;
        var unanalyzed = counted.Any(o => o.Kind == DatabaseObjectKind.Table && o.ApproximateRowCount is null);

        return $"Found {Plural(tables, "table")} and {(views == 0 ? "0 views" : Plural(views, "view"))} " +
               $"across {Plural(schemas.Count, "schema")}. " +
               $"Total approximate rows: {counted.Sum(o => o.ApproximateRowCount ?? 0)}" +
               (unanalyzed ? " (some tables not yet analyzed; run ANALYZE for accurate counts)" : "") + ". " +
               $"Total size on disk: {FormatBytes(counted.Sum(o => o.TotalSizeBytes))}." +
               (partitions > 0
                   ? $" {partitions} of the tables are partitions (PartitionOf set): their rows and size are " +
                     "counted once, in their partitioned table."
                   : "") +
               " NOTE: column names are NOT included in this response; only ColumnCount per object is available. " +
               "Do NOT infer or generate column names. Call get_table_details for exact column information.";
    }

    // ── Table details ──────────────────────────────────────────────────────────

    /// <summary>One reading of a table name as the caller wrote it.</summary>
    protected sealed record ParsedName(string Schema, string Name, string Original);

    /// <summary>
    /// The catalog rows of the requested tables, keyed by each table's real (schema, name).
    /// <see cref="ActualNames"/> maps a requested reading to that name, which differs in case on a
    /// case-insensitive database.
    /// </summary>
    protected sealed record TableCatalog(
        IReadOnlyDictionary<(string Schema, string Name), (string Schema, string Name)> ActualNames,
        IReadOnlyDictionary<(string Schema, string Name), (string Kind, string? Comment)> Tables,
        ILookup<(string Schema, string Name), ColumnDetail> Columns,
        ILookup<(string Schema, string Name), IndexDetail> Indexes,
        IReadOnlyList<ForeignKeyDetail> ForeignKeys,
        ILookup<(string Schema, string Name), CheckConstraintDetail> Checks,
        ILookup<(string Schema, string Name), UniqueConstraintDetail> Uniques);

    /// <summary>
    /// Reads the catalog for the given names. Entries sharing an <see cref="ParsedName.Original"/>
    /// are alternative readings of one requested name.
    /// </summary>
    protected abstract Task<TableCatalog> ReadTableCatalogAsync(
        DbConnection conn, IReadOnlyList<ParsedName> names, CancellationToken ct);

    public Task<TableDetailsResult> GetTableDetailsAsync(
        TableDetailsRequest request, CancellationToken ct = default) =>
        GetTableDetailsAsync(ParseTableNames(request.Tables, request.DefaultSchema), ct);

    // Catalog names are passed as pairs: joined into "schema.name" and split again, a name that
    // contains a dot would be misread.
    protected Task<TableDetailsResult> GetTableDetailsAsync(
        IReadOnlyList<ScopedTable> scope, CancellationToken ct) =>
        GetTableDetailsAsync(scope.Select(t => new ParsedName(t.Schema, t.Name, t.Qualified)).ToList(), ct);

    private Task<TableDetailsResult> GetTableDetailsAsync(IReadOnlyList<ParsedName> names, CancellationToken ct) =>
        RunAsync("table details", async () =>
        {
            await using var conn = await OpenAsync(ct);
            return BuildTableDetails(names, conn.Database, await ReadTableCatalogAsync(conn, names, ct));
        });

    /// <summary>
    /// Every reading of each name, most likely first. A dot usually separates schema from table but
    /// may belong to either ("my.schema.orders"), so each split is offered and the catalog decides.
    /// </summary>
    protected IReadOnlyList<ParsedName> ParseTableNames(string[] tables, string? defaultSchema)
    {
        var schema = defaultSchema ?? DefaultSchema;
        List<ParsedName> parsed = [];

        foreach (var table in tables)
        {
            if (Unquote(table) is { } quoted)
            {
                parsed.Add(quoted.Length == 2
                    ? new ParsedName(quoted[0], quoted[1], table)
                    : new ParsedName(schema, quoted[0], table));
                continue;
            }

            for (var dot = table.IndexOf('.'); dot >= 0; dot = table.IndexOf('.', dot + 1))
                if (dot > 0 && dot < table.Length - 1)
                    parsed.Add(new ParsedName(table[..dot], table[(dot + 1)..], table));

            parsed.Add(new ParsedName(schema, table, table));
        }

        return parsed;
    }

    /// <summary>The one or two parts of a name written with quotes ("a"."b", [a].[b]); null when it has none.</summary>
    private string[]? Unquote(string table)
    {
        var segments = SqlText.Segments(table, Dialect);
        if (segments.All(s => s.Kind != SqlSegmentKind.QuotedIdentifier)) return null;

        var parts = new List<string> { "" };
        foreach (var segment in segments)
        {
            if (segment.Kind == SqlSegmentKind.QuotedIdentifier)
            {
                parts[^1] += segment.Text;
                continue;
            }

            foreach (var c in segment.Text)
            {
                if (c == '.') parts.Add("");
                else parts[^1] += c;
            }
        }

        return parts.Count <= 2 ? [.. parts.Select(p => p.Trim())] : null;
    }

    private TableDetailsResult BuildTableDetails(IReadOnlyList<ParsedName> names, string database, TableCatalog catalog)
    {
        List<string> notFound = [];
        List<TableDetail> tables = [];
        HashSet<(string, string)> seen = [];

        foreach (var readings in names.GroupBy(n => n.Original, StringComparer.Ordinal))
        {
            var match = readings
                .Where(r => catalog.ActualNames.ContainsKey((r.Schema, r.Name)))
                .Select(r => catalog.ActualNames[(r.Schema, r.Name)])
                .Take(1)
                .ToList();

            if (match.Count == 0)
            {
                notFound.Add(readings.Key);
                continue;
            }

            var key = match[0];
            if (!seen.Add(key)) continue;

            var indexes = catalog.Indexes[key].OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
            var primaryKey = indexes.FirstOrDefault(i => i.IsPrimary);

            tables.Add(new TableDetail(
                Schema: key.Schema,
                Name: key.Name,
                Kind: catalog.Tables[key].Kind,
                Comment: catalog.Tables[key].Comment,
                Columns: [.. catalog.Columns[key]],
                PrimaryKey: primaryKey is null ? null : new PrimaryKeyInfo(primaryKey.Name, primaryKey.Columns),
                Indexes: indexes,
                ForeignKeysOutgoing: [.. catalog.ForeignKeys
                    .Where(fk => (fk.FromSchema, fk.FromTable) == key)
                    .OrderBy(fk => fk.ConstraintName, StringComparer.Ordinal)],
                ForeignKeysIncoming: [.. catalog.ForeignKeys
                    .Where(fk => (fk.ToSchema, fk.ToTable) == key)
                    .OrderBy(fk => fk.ConstraintName, StringComparer.Ordinal)],
                CheckConstraints: [.. catalog.Checks[key].OrderBy(c => c.Name, StringComparer.Ordinal)],
                UniqueConstraints: [.. catalog.Uniques[key].OrderBy(u => u.Name, StringComparer.Ordinal)]));
        }

        return new TableDetailsResult(
            Summary:
                $"Retrieved details for {tables.Count} of {Plural(tables.Count + notFound.Count, "requested table")}. " +
                $"Found {Plural(tables.Sum(t => t.Columns.Count), "column")}, " +
                $"{Plural(tables.Sum(t => t.Indexes.Count), "index", "indexes")}, " +
                $"{Plural(catalog.ForeignKeys.Count, "foreign key")}. " +
                "NOTE: view SQL bodies (SELECT definitions) are NOT included — do not infer or generate view logic. " +
                "Row data is NOT included — do not infer or generate sample values. " +
                "Tables in the NotFound list do not exist in this database — do not infer or generate their structure.",
            Dialect: Dialect.ToString(),
            Database: database,
            GeneratedAt: Now(),
            Truncated: false,
            NotFound: notFound,
            Tables: tables);
    }

    // ── Scope ──────────────────────────────────────────────────────────────────

    /// <summary>A table in scope, with the catalog facts the engines size their work by.</summary>
    protected sealed record ScopedTable(
        string Schema,
        string Name,
        long? ApproximateRowCount,
        long TotalSizeBytes,
        long IncomingForeignKeys,
        bool IsPartition = false)
    {
        public string Qualified => $"{Schema}.{Name}";
    }

    /// <summary>
    /// The tables in scope, and what to tell the caller when the request named something that is
    /// not there: an empty analysis of a mistyped schema must not read as a clean one.
    /// </summary>
    protected sealed record TableScope(IReadOnlyList<ScopedTable> Tables, string? Caveat);

    /// <summary>
    /// The user tables an operation applies to. One overview call yields both the scope and the
    /// row counts, so nothing downstream needs a second round trip to size its work.
    /// </summary>
    /// <param name="tables">Bare names to include; matched case-insensitively across schemas.</param>
    /// <param name="excludeTables">Bare names to drop after inclusion.</param>
    /// <param name="includePartitions">
    /// A partition repeats its partitioned table's structure, so the structural analyses read the
    /// parent alone unless a partition is named. The physical ones need the partitions.
    /// </param>
    protected async Task<TableScope> ResolveTableScopeAsync(
        string? schema,
        string[]? tables,
        CancellationToken ct,
        string[]? excludeTables = null,
        bool includePartitions = false)
    {
        var overview = await GetSchemaOverviewAsync(
            new SchemaOverviewRequest(SchemaFilter: schema, IncludeViews: false, IncludeSystemSchemas: false),
            ct);

        var scope = overview.Schemas
            .SelectMany(s => s.Objects.Select(o => new ScopedTable(
                s.Name, o.Name, o.ApproximateRowCount, o.TotalSizeBytes, o.IncomingForeignKeys,
                IsPartition: o.PartitionOf is not null)))
            .ToList();

        string? caveat = null;
        var where = schema is null ? "" : $" in schema '{schema}'";

        if (tables is { Length: > 0 })
        {
            var include = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
            scope = [.. scope.Where(t => include.Contains(t.Name))];

            var missing = include.Except(scope.Select(t => t.Name), StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count > 0)
                caveat = $"No table named {string.Join(", ", missing.Select(m => $"'{m}'"))} was found{where}, " +
                         "so nothing was analyzed for it. Pass bare table names, as get_schema lists them.";
        }
        else
        {
            if (!includePartitions) scope = [.. scope.Where(t => !t.IsPartition)];

            if (scope.Count == 0)
                caveat = $"No tables were found{where}: nothing was analyzed, which is not the same as nothing to report.";
        }

        if (excludeTables is { Length: > 0 })
        {
            var exclude = new HashSet<string>(excludeTables, StringComparer.OrdinalIgnoreCase);
            scope = [.. scope.Where(t => !exclude.Contains(t.Name))];
        }

        return new TableScope(scope, caveat);
    }

    /// <summary>The caveat for requested tables that could not be inspected; null when none.</summary>
    protected static string? NotFoundCaveat(TableDetailsResult details) =>
        details.NotFound.Count == 0
            ? null
            : "The following tables could not be inspected and were excluded from the analysis: " +
              string.Join(", ", details.NotFound) + ".";

    protected static IReadOnlyList<string> WithCaveats(IReadOnlyList<string> caveats, params string?[] extra) =>
        [..caveats, ..extra.OfType<string>()];

    protected static string Describe(string[]? tables) =>
        tables is null ? "*" : string.Join(",", tables);

    // ── Formatting ─────────────────────────────────────────────────────────────

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024L => $"{bytes} B",
        < 1024L * 1024L => (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024L * 1024L => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture) + " GB",
    };

    private static string Plural(int count, string singular, string? plural = null) =>
        $"{count} {(count == 1 ? singular : plural ?? singular + "s")}";

    private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
