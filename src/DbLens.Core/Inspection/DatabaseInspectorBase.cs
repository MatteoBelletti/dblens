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

    public abstract Task<bool> TestConnectionAsync(CancellationToken ct = default);

    public abstract Task<DatabaseSchemaOverview> GetSchemaOverviewAsync(
        SchemaOverviewRequest request, CancellationToken ct = default);

    public abstract Task<TableDetailsResult> GetTableDetailsAsync(
        TableDetailsRequest request, CancellationToken ct = default);

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
        long IncomingForeignKeys)
    {
        public string Qualified => $"{Schema}.{Name}";
    }

    /// <summary>
    /// The user tables an operation applies to. One overview call yields both the scope and the
    /// row counts, so nothing downstream needs a second round trip to size its work.
    /// </summary>
    /// <param name="tables">Bare names to include; matched case-insensitively across schemas.</param>
    /// <param name="excludeTables">Bare names to drop after inclusion.</param>
    protected async Task<IReadOnlyList<ScopedTable>> ResolveTableScopeAsync(
        string? schema,
        string[]? tables,
        CancellationToken ct,
        string[]? excludeTables = null)
    {
        var overview = await GetSchemaOverviewAsync(
            new SchemaOverviewRequest(SchemaFilter: schema, IncludeViews: false, IncludeSystemSchemas: false),
            ct);

        IEnumerable<ScopedTable> scope = overview.Schemas
            .SelectMany(s => s.Objects
                .Where(o => o.Kind == DatabaseObjectKind.Table)
                .Select(o => new ScopedTable(
                    s.Name, o.Name, o.ApproximateRowCount, o.TotalSizeBytes, o.IncomingForeignKeys)));

        if (tables is { Length: > 0 })
        {
            var include = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
            scope = scope.Where(t => include.Contains(t.Name));
        }

        if (excludeTables is { Length: > 0 })
        {
            var exclude = new HashSet<string>(excludeTables, StringComparer.OrdinalIgnoreCase);
            scope = scope.Where(t => !exclude.Contains(t.Name));
        }

        return scope.ToList();
    }

    protected Task<TableDetailsResult> GetTableDetailsAsync(
        IReadOnlyList<ScopedTable> scope, CancellationToken ct) =>
        GetTableDetailsAsync(new TableDetailsRequest(scope.Select(t => t.Qualified).ToArray()), ct);

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
        string? Comment);

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
                Comment: r.Comment))
            .ToList();

        return new SchemaInfo(
            Name: group.Key,
            TableCount: objects.Count(o => o.Kind == DatabaseObjectKind.Table),
            ViewCount: objects.Count(o => o.Kind == DatabaseObjectKind.View),
            Objects: objects);
    }

    /// <summary>A table name as the caller wrote it, resolved against the default schema.</summary>
    protected sealed record ParsedName(string Schema, string Name, string Original);

    protected ParsedName[] ParseTableNames(string[] tables, string? defaultSchema) =>
        tables.Select(t =>
        {
            var dot = t.IndexOf('.');
            return dot < 0
                ? new ParsedName(defaultSchema ?? DefaultSchema, t, t)
                : new ParsedName(t[..dot], t[(dot + 1)..], t);
        }).ToArray();

    public static string FormatBytes(long bytes) => bytes switch
    {
        < 1024L => $"{bytes} B",
        < 1024L * 1024L => (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024L * 1024L => (bytes / (1024.0 * 1024.0)).ToString("F1", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2", CultureInfo.InvariantCulture) + " GB",
    };

    protected ILogger Log => _log;
}
