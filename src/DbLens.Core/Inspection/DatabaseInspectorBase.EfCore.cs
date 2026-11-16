using DbLens.Core.Analysis;
using DbLens.Core.Analysis.EfCore;

namespace DbLens.Core.Inspection;

/// <summary>
/// Compares an EF Core model with the database. The model comes in already read from its snapshot;
/// everything here is catalog reads plus the history table's migration ids, so no dialect SQL is
/// needed: the history table is found by name in the schema overview and read with quoted identifiers.
/// </summary>
public abstract partial class DatabaseInspectorBase
{
    public Task<CompareEfModelResult> CompareEfModelAsync(CompareEfModelRequest request, CancellationToken ct = default) =>
        RunAsync("EF model comparison", async () =>
        {
            var model = request.Model;
            string SchemaOf(EfEntity e) => e.Schema ?? model.DefaultSchema ?? DefaultSchema;

            var schemas = request.Schema is { } only
                ? new HashSet<string>([only], StringComparer.OrdinalIgnoreCase)
                : model.Entities.Select(SchemaOf).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // One overview serves every schema and the history table's lookup.
            var everything = (await ResolveTableScopeAsync(null, null, ct)).Tables;
            var scope = everything.Where(t => schemas.Contains(t.Schema)).ToList();
            var scopeCaveats = schemas
                .Where(s => !scope.Any(t => Same(t.Schema, s)))
                .Select(s => $"No tables were found in schema '{s}': nothing was analyzed, which is not the same as nothing to report.");

            var details = await GetTableDetailsAsync(scope, ct);
            var entities = request.Schema is null
                ? model
                : model with { Entities = [.. model.Entities.Where(e => Same(SchemaOf(e), request.Schema))] };
            var match = EfModelMatcher.Match(entities, details.Tables, DefaultSchema);

            var (historyTable, applied, historyCaveat) = await ReadEfMigrationHistoryAsync(request, everything, ct);

            var result = EfDriftEngine.Analyze(entities, match, applied, historyTable, schemas);
            return result with { Caveats = WithCaveats(result.Caveats, [.. scopeCaveats, historyCaveat, NotFoundCaveat(details)]) };
        });

    /// <summary>
    /// The migration ids in the history table. These are row values, read from EF's own bookkeeping
    /// table only: the one read of data outside query_with_receipt, declared in docs/safety.md.
    /// </summary>
    private async Task<((string Schema, string Name)? Table, IReadOnlyList<string>? Applied, string? Caveat)> ReadEfMigrationHistoryAsync(
        CompareEfModelRequest request, IReadOnlyList<ScopedTable> tables, CancellationToken ct)
    {
        var candidates = tables
            .Where(t => Same(t.Name, request.HistoryTable) && (request.HistorySchema is null || Same(t.Schema, request.HistorySchema)))
            .ToList();

        if (candidates.Count == 0) return (null, null, null);

        // EF writes the history table unqualified, so it lands in the login's default schema; the
        // model's own default schema is the other place it is configured to.
        string[] preferred = [.. new[] { request.Model.DefaultSchema, DefaultSchema }.OfType<string>()];
        var chosen = candidates.Count == 1
            ? candidates[0]
            : candidates.FirstOrDefault(c => preferred.Any(p => Same(p, c.Schema)));

        if (chosen is null)
            return (null, null,
                $"Several {request.HistoryTable} tables exist ({string.Join(", ", candidates.Select(c => c.Qualified))}), " +
                "so applied migrations were not read: pass historySchema to name the right one.");

        await using var conn = await OpenAsync(ct);
        var sql = $"SELECT {SqlIdentifier.Quote("MigrationId", Dialect)} AS migration_id " +
                  $"FROM {SqlIdentifier.Qualify(chosen.Schema, chosen.Name, Dialect)}";
        var applied = await ReadAllAsync(Command(conn, sql), r => r.Text("migration_id"), ct);

        var caveat = candidates.Count > 1
            ? $"Several {request.HistoryTable} tables exist; {chosen.Qualified} was read. Pass historySchema to read another."
            : null;
        return ((chosen.Schema, chosen.Name), applied, caveat);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
