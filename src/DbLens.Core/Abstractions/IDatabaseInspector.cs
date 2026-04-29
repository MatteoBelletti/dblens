namespace DbLens.Core.Abstractions;

/// <summary>
/// Contract for dialect-specific database inspection.
/// Implementations must be registered as keyed services using <see cref="DbLens.Core.Models.DatabaseDialect"/> as the key.
/// </summary>
public interface IDatabaseInspector
{
    /// <summary>The dialect this inspector targets.</summary>
    DbLens.Core.Models.DatabaseDialect Dialect { get; }

    /// <summary>Opens a connection and executes a trivial query to confirm reachability.</summary>
    Task<bool> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>Returns a high-level overview of all user tables and views, grouped by schema.</summary>
    Task<DbLens.Core.Models.DatabaseSchemaOverview> GetSchemaOverviewAsync(
        DbLens.Core.Models.SchemaOverviewRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Returns full detail (columns, indexes, FKs, constraints) for one or more specific tables.
    /// </summary>
    Task<DbLens.Core.Models.TableDetailsResult> GetTableDetailsAsync(
        DbLens.Core.Models.TableDetailsRequest request,
        CancellationToken ct = default);
}
