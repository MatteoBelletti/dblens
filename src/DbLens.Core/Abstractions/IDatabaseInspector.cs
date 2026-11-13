using DbLens.Core.Analysis.Cascade;
using DbLens.Core.Analysis.Denormalization;
using DbLens.Core.Analysis.Fragmentation;
using DbLens.Core.Analysis.Gdpr;
using DbLens.Core.Analysis.Indexes;
using DbLens.Core.Analysis.Migration;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Analysis.Relationships;

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
    /// Runs schema-only denormalization heuristics over table structure.
    /// Rules: denorm-repeating-group, denorm-derived-column, denorm-duplicated-column,
    ///        denorm-multivalue-column, denorm-wide-table.
    /// </summary>
    Task<AnalyzeDenormalizationResult> AnalyzeDenormalizationAsync(
        AnalyzeDenormalizationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Reads physical index statistics and reports fragmentation.
    /// Requires pgstattuple on PostgreSQL; uses sys.dm_db_index_physical_stats on SQL Server.
    /// Rules: idx-fragmentation-high, idx-fragmentation-moderate, idx-low-page-density.
    /// </summary>
    Task<AnalyzeFragmentationResult> AnalyzeFragmentationAsync(
        AnalyzeFragmentationRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the execution plan for a query, with rule-based findings over its shape.
    /// When <see cref="ExplainQueryRequest.Analyze"/> is true the query is executed to collect
    /// real row counts; implementations must reject anything that is not a pure SELECT via
    /// <see cref="QueryPlan.StatementGuard"/> and run it inside a transaction that is rolled back.
    /// Rules: plan-seq-scan-large-table, plan-estimate-mismatch, plan-nested-loop-large-input,
    ///        plan-sort-spilled-to-disk, plan-scan-with-filter.
    /// </summary>
    Task<ExplainQueryResult> ExplainQueryAsync(
        ExplainQueryRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Classifies the statements of a migration script and reports their lock and data impact,
    /// weighted by the real size of the tables they touch. The script is never executed.
    /// Rules: migration-table-rewrite, migration-blocking-lock, migration-index-not-concurrent,
    ///        migration-validated-constraint-scan, migration-destructive, migration-summary.
    /// </summary>
    Task<CheckMigrationSafetyResult> CheckMigrationSafetyAsync(
        CheckMigrationSafetyRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Maps how tables join: declared foreign keys plus references the schema does not declare,
    /// inferred from naming and types. With VerifyCandidates, each inferred reference is tested with
    /// a read-only aggregate query counting values that match no parent row.
    /// Rules: relationship-undeclared-fk.
    /// </summary>
    Task<InferRelationshipsResult> InferRelationshipsAsync(
        InferRelationshipsRequest request,
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
