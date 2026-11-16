using DbLens.Core.Analysis;
using DbLens.Core.Analysis.EfCore;
using DbLens.Core.Models;

namespace DbLens.Core.Inspection;

/// <summary>
/// Compares an EF Core model with the database. The model comes in already read from its snapshot;
/// everything here is catalog reads, so no dialect SQL is needed: the history table is found by name
/// in the schema overview and read with quoted identifiers.
/// </summary>
public abstract partial class DatabaseInspectorBase
{
    public Task<CompareEfModelResult> CompareEfModelAsync(CompareEfModelRequest request, CancellationToken ct = default) =>
        RunAsync("EF model comparison", async () =>
        {
            var model = request.Model;
            var schemas = request.Schema is { } only
                ? new HashSet<string>([only], StringComparer.OrdinalIgnoreCase)
                : model.Entities.Select(e => e.Schema ?? model.DefaultSchema ?? DefaultSchema)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<ScopedTable> scope = [];
            List<string?> scopeCaveats = [];
            foreach (var schema in schemas)
            {
                var resolved = await ResolveTableScopeAsync(schema, null, ct);
                scope.AddRange(resolved.Tables);
                scopeCaveats.Add(resolved.Caveat);
            }

            var details = await GetTableDetailsAsync(scope, ct);
            var entities = request.Schema is null
                ? model
                : model with { Entities = [.. model.Entities.Where(e => Same(e.Schema ?? model.DefaultSchema ?? DefaultSchema, request.Schema))] };
            var match = EfModelMatcher.Match(entities, details.Tables, DefaultSchema);

            var (historyTable, applied, historyCaveat) = await ReadEfMigrationHistoryAsync(request, ct);

            var result = EfDriftEngine.Analyze(entities, match, applied, historyTable, schemas);
            return result with { Caveats = WithCaveats(result.Caveats, [.. scopeCaveats, historyCaveat, NotFoundCaveat(details)]) };
        });

    private async Task<(string? Table, IReadOnlyList<string>? Applied, string? Caveat)> ReadEfMigrationHistoryAsync(
        CompareEfModelRequest request, CancellationToken ct)
    {
        var overview = await GetSchemaOverviewAsync(
            new SchemaOverviewRequest(SchemaFilter: request.HistorySchema, IncludeViews: false), ct);

        var candidates = overview.Schemas
            .SelectMany(s => s.Objects.Where(o => Same(o.Name, request.HistoryTable)).Select(o => (Schema: s.Name, o.Name)))
            .ToList();

        if (candidates.Count == 0) return (null, null, null);

        // EF writes the history table unqualified, so it lands in the login's default schema; the
        // model's own default schema is the other place it is configured to.
        var preferred = new[] { request.Model.DefaultSchema, DefaultSchema }.OfType<string>().ToList();
        var chosen = candidates.Count == 1
            ? candidates[0]
            : candidates.FirstOrDefault(c => preferred.Any(p => Same(p, c.Schema)));

        if (chosen == default)
            return (null, null,
                $"Several {request.HistoryTable} tables exist ({string.Join(", ", candidates.Select(c => $"{c.Schema}.{c.Name}"))}), " +
                "so applied migrations were not read. Name the right one's schema to compare them.");

        await using var conn = await OpenAsync(ct);
        var sql = $"SELECT {SqlIdentifier.Quote("MigrationId", Dialect)} AS migration_id " +
                  $"FROM {SqlIdentifier.Qualify(chosen.Schema, chosen.Name, Dialect)}";
        var applied = await ReadAllAsync(Command(conn, sql), r => r.Text("migration_id"), ct);

        var caveat = candidates.Count > 1
            ? $"Several {request.HistoryTable} tables exist; {chosen.Schema}.{chosen.Name} was read."
            : null;
        return ($"{chosen.Schema}.{chosen.Name}", applied, caveat);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
