using DbLens.Core.Analysis.Glossary;
using DbLens.Core.Analysis.Relationships;
using DbLens.Core.Analysis.Verification;
using DbLens.Core.Models;
using Microsoft.Extensions.Logging;

namespace DbLens.Core.Inspection;

/// <summary>
/// infer_relationships and build_glossary. The catalog comes from GetTableDetailsAsync, join evidence
/// from the SQL the dialect can read, and the inclusion probes are generated in Core and run by the
/// shared verification loop.
/// </summary>
public abstract partial class DatabaseInspectorBase
{
    /// <summary>
    /// View and routine definitions plus workload statements (pg_stat_statements, Query Store).
    /// A source that cannot be read is a caveat, never an error.
    /// </summary>
    protected abstract Task<SqlSourceSet> QuerySqlSourcesAsync(CancellationToken ct);

    public Task<InferRelationshipsResult> InferRelationshipsAsync(
        InferRelationshipsRequest request, CancellationToken ct = default)
    {
        Log.LogInformation(
            "Inferring relationships (schema={Schema}, tables={Tables}, verify={Verify})",
            request.Schema ?? "*", Describe(request.Tables), request.VerifyCandidates);

        return RunAsync("relationship inference", async () =>
        {
            var (scope, scopeCaveat) = await ResolveTableScopeAsync(request.Schema, request.Tables, ct);
            if (scope.Count == 0) return RelationshipAnalysisEngine.Empty() with { Caveats = [scopeCaveat!] };

            var details = await GetTableDetailsAsync(scope, ct);
            var (result, _) = await MapRelationshipsAsync(scope, details.Tables, request.VerifyCandidates, request.Budget, ct);
            result = result with { Caveats = WithCaveats(result.Caveats, scopeCaveat, NotFoundCaveat(details)) };

            Log.LogInformation(
                "Relationship inference completed: {Declared} declared, {Inferred} inferred, {Verified} verified",
                result.Summary.DeclaredRelationships, result.Summary.InferredRelationships,
                result.Summary.VerifiedRelationships);

            return result;
        });
    }

    public Task<GlossaryResult> BuildGlossaryAsync(GlossaryRequest request, CancellationToken ct = default)
    {
        Log.LogInformation("Building glossary (schema={Schema}, tables={Tables})", request.Schema ?? "*", Describe(request.Tables));

        return RunAsync("glossary", async () =>
        {
            var (scope, scopeCaveat) = await ResolveTableScopeAsync(request.Schema, request.Tables, ct);
            if (scope.Count == 0)
                throw new ArgumentException("No tables matched the requested schema and tables.");

            var details = await GetTableDetailsAsync(scope, ct);
            var (relationships, sources) = await MapRelationshipsAsync(
                scope, details.Tables, request.VerifyJoins, new InferRelationshipsRequest().Budget, ct);

            var result = GlossaryEngine.Build(
                DatabaseName, Dialect, details.Tables,
                scope.ToDictionary(t => (t.Schema, t.Name), t => t.ApproximateRowCount),
                relationships, sources.Sources, request.Certified ?? [], DateTimeOffset.UtcNow);

            return result with
            {
                Caveats = WithCaveats([..result.Caveats, ..sources.Caveats], scopeCaveat, NotFoundCaveat(details)),
            };
        });
    }

    private async Task<(InferRelationshipsResult Result, SqlSourceSet Sources)> MapRelationshipsAsync(
        IReadOnlyList<ScopedTable> scope,
        IReadOnlyList<TableDetail> tables,
        bool verify,
        VerificationBudget budget,
        CancellationToken ct)
    {
        var sources = await ReadSqlSourcesAsync(ct);
        var joins = JoinPredicateExtractor.Extract(sources.Sources, tables, DefaultSchema, Dialect);

        var analysis = RelationshipAnalysisEngine.Analyze(tables, joins, sources.Sources.Count);
        analysis = analysis with { Result = analysis.Result with { Caveats = [..analysis.Result.Caveats, ..sources.Caveats] } };
        if (!verify) return (analysis.Result, sources);

        var rowCounts = scope.ToDictionary(t => (t.Schema, t.Name), t => t.ApproximateRowCount);
        var plan = RelationshipAnalysisEngine.BuildPlan(analysis, rowCounts, Dialect);
        var measurements = await RunVerificationProbesAsync(plan, budget, ct);

        var result = RelationshipAnalysisEngine.ApplyVerification(analysis, measurements, Dialect, budget);
        if (VerificationCaveats.Count > 0)
            result = result with { Caveats = [..result.Caveats, ..VerificationCaveats] };

        return (result, sources);
    }

    private async Task<SqlSourceSet> ReadSqlSourcesAsync(CancellationToken ct)
    {
        var read = await QuerySqlSourcesAsync(ct);

        // dblens's own probes land in the workload history too; counting them would let a guess
        // confirm itself.
        return read with
        {
            Sources = [.. read.Sources.Where(s => s.Kind != SqlSourceKind.Workload || !ProbeColumns.IsProbe(s.Sql))],
        };
    }
}
