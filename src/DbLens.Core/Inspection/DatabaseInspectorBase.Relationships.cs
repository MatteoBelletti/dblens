using DbLens.Core.Analysis.Relationships;
using Microsoft.Extensions.Logging;

namespace DbLens.Core.Inspection;

/// <summary>
/// infer_relationships. Entirely dialect-independent: the catalog comes from GetTableDetailsAsync,
/// and the inclusion probes are generated in Core and executed by the shared verification loop.
/// </summary>
public abstract partial class DatabaseInspectorBase
{
    public Task<InferRelationshipsResult> InferRelationshipsAsync(
        InferRelationshipsRequest request, CancellationToken ct = default)
    {
        Log.LogInformation(
            "Inferring relationships (schema={Schema}, tables={Tables}, verify={Verify})",
            request.Schema ?? "*", Describe(request.Tables), request.VerifyCandidates);

        return RunAsync("relationship inference", async () =>
        {
            var scope = await ResolveTableScopeAsync(request.Schema, request.Tables, ct);
            if (scope.Count == 0) return RelationshipAnalysisEngine.Empty();

            var details = await GetTableDetailsAsync(scope, ct);
            var analysis = RelationshipAnalysisEngine.Analyze(details.Tables);
            var result = analysis.Result;

            if (request.VerifyCandidates)
            {
                var rowCounts = scope.ToDictionary(t => (t.Schema, t.Name), t => t.ApproximateRowCount);
                var plan = RelationshipAnalysisEngine.BuildPlan(analysis, rowCounts, Dialect);
                var measurements = await RunVerificationProbesAsync(plan, request.Budget, ct);

                result = RelationshipAnalysisEngine.ApplyVerification(analysis, measurements, Dialect);
                if (VerificationCaveats.Count > 0)
                    result = result with { Caveats = [..result.Caveats, ..VerificationCaveats] };
            }

            result = result with { Caveats = WithCaveat(result.Caveats, NotFoundCaveat(details)) };

            Log.LogInformation(
                "Relationship inference completed: {Declared} declared, {Inferred} inferred, {Verified} verified",
                result.Summary.DeclaredRelationships, result.Summary.InferredRelationships,
                result.Summary.VerifiedRelationships);

            return result;
        });
    }
}
