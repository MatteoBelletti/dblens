using DbLens.Core.Abstractions;
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
using DbLens.Mcp.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbLens.Tests;

/// <summary>
/// An inspector for tool tests: every call is unsupported except the one a test overrides. A new
/// IDatabaseInspector method gets its throwing default here, once.
/// </summary>
internal class StubInspector : IDatabaseInspector
{
    public DatabaseDialect Dialect => DatabaseDialect.PostgreSql;
    public string DatabaseName => "stub";
    public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
    public virtual Task<DatabaseSchemaOverview> GetSchemaOverviewAsync(SchemaOverviewRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public virtual Task<TableDetailsResult> GetTableDetailsAsync(TableDetailsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AnalyzeIndexesResult> AnalyzeIndexesAsync(AnalyzeIndexesRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ScanGdprResult> ScanGdprAsync(ScanGdprRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AnalyzeDenormalizationResult> AnalyzeDenormalizationAsync(AnalyzeDenormalizationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AnalyzeFragmentationResult> AnalyzeFragmentationAsync(AnalyzeFragmentationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ExplainQueryResult> ExplainQueryAsync(ExplainQueryRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CheckMigrationSafetyResult> CheckMigrationSafetyAsync(CheckMigrationSafetyRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<InferRelationshipsResult> InferRelationshipsAsync(InferRelationshipsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<QueryWithReceiptResult> QueryWithReceiptAsync(QueryWithReceiptRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<GlossaryResult> BuildGlossaryAsync(GlossaryRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CheckCascadePathsResult> CheckCascadePathsAsync(CheckCascadePathsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<CompareEfModelResult> CompareEfModelAsync(CompareEfModelRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<FindHotQueriesResult> FindHotQueriesAsync(FindHotQueriesRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AnalyzeTableHygieneResult> AnalyzeTableHygieneAsync(AnalyzeTableHygieneRequest request, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>Resolves the configured inspectors as the real DI container does: by dialect key.</summary>
internal sealed class StubServices(Func<IDatabaseInspector> inspector, Action<DatabaseDialect>? onResolve = null)
    : IServiceProvider, IKeyedServiceProvider
{
    // Both dialects answer, as with the demo fallback on: the stub inspector stands in for either.
    public object? GetService(Type serviceType) =>
        serviceType == typeof(ConfiguredDatabases)
            ? new ConfiguredDatabases(new ConfigurationBuilder()
                .AddInMemoryCollection([new("DbLens:DemoFallback", "true")]).Build())
            : null;

    public object? GetKeyedService(Type serviceType, object? serviceKey) => GetRequiredKeyedService(serviceType, serviceKey);

    public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
    {
        if (serviceKey is DatabaseDialect d) onResolve?.Invoke(d);
        return inspector();
    }
}

/// <summary>Builds the inspector for a caller-supplied connection string.</summary>
internal sealed class StubFactory(Func<IDatabaseInspector> inspector, Action<DatabaseDialect, string>? onCreate = null)
    : IDatabaseInspectorFactory
{
    public IDatabaseInspector Create(DatabaseDialect dialect, string connectionString)
    {
        onCreate?.Invoke(dialect, connectionString);
        return inspector();
    }
}
