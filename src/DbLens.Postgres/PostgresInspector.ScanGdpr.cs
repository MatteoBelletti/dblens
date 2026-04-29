using DbLens.Core.Analysis.Gdpr;
using DbLens.Core.Models;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    public async Task<ScanGdprResult> ScanGdprAsync(
        ScanGdprRequest request,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Starting GDPR scan (schema={Schema}, tables={Tables}, minConfidence={MinConfidence})",
            request.Schema ?? "*",
            request.Tables is null ? "*" : string.Join(",", request.Tables),
            request.MinConfidence);

        try
        {
            var scope = await ResolveScanScopeAsync(request, ct);
            if (scope.Count == 0)
                return GdprAnalysisEngine.Empty();

            var tableNames = scope.Select(t => $"{t.Schema}.{t.Name}").ToArray();
            var details = await GetTableDetailsAsync(new TableDetailsRequest(tableNames), ct);

            var result = GdprAnalysisEngine.Analyze(details.Tables, request, Dialect);

            _logger.LogInformation(
                "GDPR scan completed: {FindingCount} findings", result.Findings.Count);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NpgsqlException ex)
        {
            throw new DatabaseInspectionException(
                $"PostgreSQL error during GDPR scan: {ex.Message}", ex);
        }
    }

    private async Task<List<(string Schema, string Name)>> ResolveScanScopeAsync(
        ScanGdprRequest request,
        CancellationToken ct)
    {
        var overview = await GetSchemaOverviewAsync(
            new SchemaOverviewRequest(
                SchemaFilter: request.Schema,
                IncludeViews: false,
                IncludeSystemSchemas: false),
            ct);

        var excludeSet = request.ExcludeTables is { Length: > 0 }
            ? new HashSet<string>(request.ExcludeTables, StringComparer.OrdinalIgnoreCase)
            : null;

        IEnumerable<(string Schema, string Name)> scope = overview.Schemas
            .SelectMany(s => s.Objects
                .Where(o => o.Kind == DatabaseObjectKind.Table)
                .Select(o => (Schema: s.Name, Name: o.Name)));

        if (request.Tables is { Length: > 0 })
        {
            var filter = new HashSet<string>(request.Tables, StringComparer.OrdinalIgnoreCase);
            scope = scope.Where(t => filter.Contains(t.Name));
        }

        if (excludeSet is not null)
            scope = scope.Where(t => !excludeSet.Contains(t.Name));

        return scope.ToList();
    }
}
