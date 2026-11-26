using DbLens.Core.Analysis.Cascade;
using DbLens.Core.Analysis.Denormalization;
using DbLens.Core.Analysis.EfCore;
using DbLens.Core.Analysis.Fragmentation;
using DbLens.Core.Analysis.Gdpr;
using DbLens.Core.Analysis.Glossary;
using DbLens.Core.Analysis.HotQueries;
using DbLens.Core.Analysis.Hygiene;
using DbLens.Core.Analysis.Indexes;
using DbLens.Core.Analysis.Migration;
using DbLens.Core.Analysis.QueryPlan;
using DbLens.Core.Analysis.Receipts;
using DbLens.Core.Analysis.Relationships;
using DbLens.Core.Models;

namespace DbLens.Core.Abstractions;

/// <summary>
/// What dblens can ask of a database. One implementation per dialect, registered as a keyed
/// service under its <see cref="DatabaseDialect"/>. Each tool's rules are listed in docs/tools.md.
/// </summary>
public interface IDatabaseInspector
{
    DatabaseDialect Dialect { get; }

    /// <summary>The database the connection string names. Read without connecting.</summary>
    string DatabaseName { get; }

    Task<bool> TestConnectionAsync(CancellationToken ct = default);

    /// <summary>The database this inspector reaches, read from its connection string without the password.</summary>
    Target Describe(TargetSource source);

    /// <summary>Connects and returns the server's own version string.</summary>
    Task<string> GetServerVersionAsync(CancellationToken ct = default);

    Task<DatabaseSchemaOverview> GetSchemaOverviewAsync(SchemaOverviewRequest request, CancellationToken ct = default);

    Task<TableDetailsResult> GetTableDetailsAsync(TableDetailsRequest request, CancellationToken ct = default);

    Task<AnalyzeIndexesResult> AnalyzeIndexesAsync(AnalyzeIndexesRequest request, CancellationToken ct = default);

    Task<ScanGdprResult> ScanGdprAsync(ScanGdprRequest request, CancellationToken ct = default);

    Task<AnalyzeDenormalizationResult> AnalyzeDenormalizationAsync(
        AnalyzeDenormalizationRequest request, CancellationToken ct = default);

    Task<AnalyzeFragmentationResult> AnalyzeFragmentationAsync(
        AnalyzeFragmentationRequest request, CancellationToken ct = default);

    /// <summary>
    /// With <see cref="ExplainQueryRequest.Analyze"/> the query is executed: only a pure SELECT,
    /// in a transaction that is rolled back.
    /// </summary>
    Task<ExplainQueryResult> ExplainQueryAsync(ExplainQueryRequest request, CancellationToken ct = default);

    /// <summary>The script is classified, never executed.</summary>
    Task<CheckMigrationSafetyResult> CheckMigrationSafetyAsync(
        CheckMigrationSafetyRequest request, CancellationToken ct = default);

    Task<InferRelationshipsResult> InferRelationshipsAsync(
        InferRelationshipsRequest request, CancellationToken ct = default);

    /// <summary>The one operation that returns values from the data.</summary>
    Task<QueryWithReceiptResult> QueryWithReceiptAsync(QueryWithReceiptRequest request, CancellationToken ct = default);

    Task<GlossaryResult> BuildGlossaryAsync(GlossaryRequest request, CancellationToken ct = default);

    Task<CheckCascadePathsResult> CheckCascadePathsAsync(
        CheckCascadePathsRequest request, CancellationToken ct = default);

    Task<CompareEfModelResult> CompareEfModelAsync(CompareEfModelRequest request, CancellationToken ct = default);

    Task<FindHotQueriesResult> FindHotQueriesAsync(FindHotQueriesRequest request, CancellationToken ct = default);

    Task<AnalyzeTableHygieneResult> AnalyzeTableHygieneAsync(AnalyzeTableHygieneRequest request, CancellationToken ct = default);
}
