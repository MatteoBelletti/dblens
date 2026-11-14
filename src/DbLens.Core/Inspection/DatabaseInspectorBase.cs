using System.Data.Common;
using System.Globalization;
using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using Microsoft.Extensions.Logging;

namespace DbLens.Core.Inspection;

/// <summary>
/// Everything an inspector does that does not depend on the SQL dialect.
///
/// A dialect supplies connections, catalog queries and plan parsing; this class owns the
/// orchestration around them — scope resolution, error wrapping, the NotFound caveat, the
/// explain_query statement guard, and the signal verification loop. Built on System.Data.Common
/// so it needs no driver: both NpgsqlException and SqlException are DbExceptions.
///
/// The point is less line count than enforcement. A guard or caveat implemented once here cannot
/// be forgotten by a dialect, and the two dialects cannot drift apart in behaviour the parity
/// tests would only catch against a live database.
/// </summary>
public abstract partial class DatabaseInspectorBase : IDatabaseInspector
{
    /// <summary>
    /// Applied to every command. Npgsql's default is 0 (infinite) unless set in the connection
    /// string; SqlClient's is 30 seconds but is set explicitly so the intent is visible.
    /// </summary>
    protected const int QueryTimeoutSeconds = 30;

    private readonly ILogger _log;

    protected DatabaseInspectorBase(ILogger logger)
    {
        _log = logger;
    }

    public abstract DatabaseDialect Dialect { get; }

    /// <summary>Engine name for messages, e.g. "PostgreSQL" or "SQL Server".</summary>
    protected abstract string DialectDisplayName { get; }

    /// <summary>Schema used when a table name carries no prefix: "public" or "dbo".</summary>
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

    public abstract Task<bool> TestConnectionAsync(CancellationToken ct = default);

    public abstract Task<DatabaseSchemaOverview> GetSchemaOverviewAsync(
        SchemaOverviewRequest request, CancellationToken ct = default);

    public Task<TableDetailsResult> GetTableDetailsAsync(
        TableDetailsRequest request, CancellationToken ct = default) =>
        GetTableDetailsAsync(ParseTableNames(request.Tables, request.DefaultSchema), ct);

    /// <summary>
    /// Reads the catalog for the given names. Entries sharing an <see cref="ParsedName.Original"/>
    /// are alternative readings of one requested name; the first that exists is the table.
    /// </summary>
    protected abstract Task<TableDetailsResult> GetTableDetailsAsync(
        IReadOnlyList<ParsedName> names, CancellationToken ct);

    // ── Error handling ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs an operation and wraps driver errors in <see cref="DatabaseInspectionException"/>.
    /// Cancellation passes through untouched: it is not a DbException, so it is never caught.
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
    /// The user tables an operation applies to. One overview call yields both the scope and the
    /// row counts, so nothing downstream needs a second round trip to size its work.
    /// </summary>
    /// <param name="tables">Bare names to include; matched case-insensitively across schemas.</param>
    /// <param name="excludeTables">Bare names to drop after inclusion.</param>
    /// <param name="includePartitions">
    /// A partition holds rows of its partitioned table and repeats its structure, so the structural
    /// analyses read the parent alone unless a partition is named. Physical ones need the partitions.
    /// </param>
    protected async Task<IReadOnlyList<ScopedTable>> ResolveTableScopeAsync(
        string? schema,
        string[]? tables,
        CancellationToken ct,
        string[]? excludeTables = null,
        bool includePartitions = false)
    {
        var overview = await GetSchemaOverviewAsync(
            new SchemaOverviewRequest(SchemaFilter: schema, IncludeViews: false, IncludeSystemSchemas: false),
            ct);

        IEnumerable<ScopedTable> scope = overview.Schemas
            .SelectMany(s => s.Objects
                .Where(o => o.Kind == DatabaseObjectKind.Table)
                .Select(o => new ScopedTable(
                    s.Name, o.Name, o.ApproximateRowCount, o.TotalSizeBytes, o.IncomingForeignKeys,
                    IsPartition: o.PartitionOf is not null)));

        if (tables is { Length: > 0 })
        {
            var include = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
            scope = scope.Where(t => include.Contains(t.Name));
        }
        else if (!includePartitions)
        {
            scope = scope.Where(t => !t.IsPartition);
        }

        if (excludeTables is { Length: > 0 })
        {
            var exclude = new HashSet<string>(excludeTables, StringComparer.OrdinalIgnoreCase);
            scope = scope.Where(t => !exclude.Contains(t.Name));
        }

        return scope.ToList();
    }

    // Names from the catalog are passed as they are: joining them into "schema.name" and splitting
    // again would misread any name that contains a dot.
    protected Task<TableDetailsResult> GetTableDetailsAsync(
        IReadOnlyList<ScopedTable> scope, CancellationToken ct) =>
        GetTableDetailsAsync(scope.Select(t => new ParsedName(t.Schema, t.Name, t.Qualified)).ToList(), ct);

    /// <summary>
    /// The caveat every diagnostic appends when some requested tables could not be inspected.
    /// Null when nothing was missing.
    /// </summary>
    protected static string? NotFoundCaveat(TableDetailsResult details) =>
        details.NotFound.Count == 0
            ? null
            : "The following tables could not be inspected and were excluded from the analysis: " +
              string.Join(", ", details.NotFound) + ".";

    protected static IReadOnlyList<string> WithCaveat(IReadOnlyList<string> caveats, string? extra) =>
        extra is null ? caveats : [..caveats, extra];

    protected static string Describe(string[]? tables) =>
        tables is null ? "*" : string.Join(",", tables);

    // ── Shared catalog helpers for GetSchemaOverviewAsync / GetTableDetailsAsync ──

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

    protected static SchemaInfo BuildSchemaInfo(IGrouping<string, OverviewRow> group)
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

    /// <summary>One reading of a table name as the caller wrote it.</summary>
    protected sealed record ParsedName(string Schema, string Name, string Original);

    /// <summary>
    /// Every reading of each name, most likely first. A dot usually separates schema from table, but
    /// it may belong to either ("my.schema.orders"), so each split is offered and the catalog decides.
    /// </summary>
    protected IReadOnlyList<ParsedName> ParseTableNames(string[] tables, string? defaultSchema)
    {
        var parsed = new List<ParsedName>();

        foreach (var table in tables)
        {
            for (var dot = table.IndexOf('.'); dot >= 0; dot = table.IndexOf('.', dot + 1))
                if (dot > 0 && dot < table.Length - 1)
                    parsed.Add(new ParsedName(table[..dot], table[(dot + 1)..], table));

            parsed.Add(new ParsedName(defaultSchema ?? DefaultSchema, table, table));
        }

        return parsed;
    }

    /// <summary>The catalog rows of a table-details read, keyed by the table's real (schema, name).</summary>
    protected sealed record TableCatalog(
        string? Database,
        IReadOnlyDictionary<(string Schema, string Name), (string Schema, string Name)> ActualNames,
        IReadOnlyDictionary<(string Schema, string Name), (string Kind, string? Comment)> Tables,
        IReadOnlyDictionary<(string Schema, string Name), List<ColumnDetail>> Columns,
        IReadOnlyDictionary<(string Schema, string Name), List<IndexDetail>> Indexes,
        IReadOnlyList<ForeignKeyDetail> ForeignKeys,
        IReadOnlyDictionary<(string Schema, string Name), List<CheckConstraintDetail>> Checks,
        IReadOnlyDictionary<(string Schema, string Name), List<UniqueConstraintDetail>> Uniques);

    /// <summary>
    /// Assembles the result in request order. <see cref="TableCatalog.ActualNames"/> maps a requested
    /// reading to the name the catalog holds, which differs in case on a case-insensitive database.
    /// </summary>
    protected TableDetailsResult BuildTableDetails(IReadOnlyList<ParsedName> names, TableCatalog catalog)
    {
        var notFound = new List<string>();
        var tables = new List<TableDetail>();
        var seen = new HashSet<(string, string)>();

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

            var info = catalog.Tables[key];
            var indexes = catalog.Indexes.GetValueOrDefault(key, []).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
            var primaryKey = indexes.FirstOrDefault(i => i.IsPrimary);

            tables.Add(new TableDetail(
                Schema: key.Schema,
                Name: key.Name,
                Kind: info.Kind,
                Comment: info.Comment,
                Columns: catalog.Columns.GetValueOrDefault(key, []),
                PrimaryKey: primaryKey is null ? null : new PrimaryKeyInfo(primaryKey.Name, primaryKey.Columns),
                Indexes: indexes,
                ForeignKeysOutgoing: catalog.ForeignKeys
                    .Where(fk => (fk.FromSchema, fk.FromTable) == key)
                    .OrderBy(fk => fk.ConstraintName, StringComparer.Ordinal).ToList(),
                ForeignKeysIncoming: catalog.ForeignKeys
                    .Where(fk => (fk.ToSchema, fk.ToTable) == key)
                    .OrderBy(fk => fk.ConstraintName, StringComparer.Ordinal).ToList(),
                CheckConstraints: catalog.Checks.GetValueOrDefault(key, []).OrderBy(c => c.Name, StringComparer.Ordinal).ToList(),
                UniqueConstraints: catalog.Uniques.GetValueOrDefault(key, []).OrderBy(u => u.Name, StringComparer.Ordinal).ToList()));
        }

        var requested = tables.Count + notFound.Count;
        var totalColumns = tables.Sum(t => t.Columns.Count);
        var totalIndexes = tables.Sum(t => t.Indexes.Count);
        var totalFks = catalog.ForeignKeys.Count;

        return new TableDetailsResult(
            Summary:
                $"Retrieved details for {tables.Count} of {requested} requested table{(requested != 1 ? "s" : "")}. " +
                $"Found {totalColumns} column{(totalColumns != 1 ? "s" : "")}, " +
                $"{totalIndexes} index{(totalIndexes != 1 ? "es" : "")}, " +
                $"{totalFks} foreign key{(totalFks != 1 ? "s" : "")}. " +
                "NOTE: view SQL bodies (SELECT definitions) are NOT included — do not infer or generate view logic. " +
                "Row data is NOT included — do not infer or generate sample values. " +
                "Tables in the NotFound list do not exist in this database — do not infer or generate their structure.",
            Dialect: Dialect.ToString(),
            Database: catalog.Database ?? "unknown",
            GeneratedAt: DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Truncated: false,
            NotFound: notFound,
            Tables: tables);
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024L => $"{bytes} B",
        < 1024L * 1024L => (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024L * 1024L => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture) + " GB",
    };

    protected ILogger Log => _log;
}
