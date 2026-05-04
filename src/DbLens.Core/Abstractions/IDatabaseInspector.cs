using DbLens.Core.Analysis.Cascade;
using DbLens.Core.Analysis.Gdpr;
using DbLens.Core.Analysis.Indexes;

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

    /// <summary>
    /// Runs rule-based index analysis and returns diagnostic findings.
    /// Rules: fk-without-index, duplicate-index, redundant-index, unused-index.
    /// </summary>
    Task<AnalyzeIndexesResult> AnalyzeIndexesAsync(
        AnalyzeIndexesRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Scans table columns for potential PII/GDPR-relevant data using name-pattern matching.
    /// Rules: pii-identifier, pii-contact, pii-financial, pii-special-category, pii-location, missing-gdpr-controls.
    /// </summary>
    Task<ScanGdprResult> ScanGdprAsync(
        ScanGdprRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Traces ON DELETE CASCADE chains from a starting table (or all cascade roots when unspecified).
    /// Rules: cascade-circular-path, cascade-deep-path, cascade-breaks-with-set-null,
    ///        cascade-breaks-with-restrict, cascade-self-referencing, cascade-summary.
    /// </summary>
    Task<CheckCascadePathsResult> CheckCascadePathsAsync(
        CheckCascadePathsRequest request,
        CancellationToken ct = default);
}
