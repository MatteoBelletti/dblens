using DbLens.Core.Models;

namespace DbLens.Core.Analysis.EfCore;

/// <summary>
/// A property and the column it found. <see cref="Column"/> is null when the table has none by that
/// name, or when several differ from it only in case (<see cref="Ambiguous"/>): then nothing is known.
/// </summary>
public sealed record EfColumnMatch(EfEntity Entity, EfProperty Property, ColumnDetail? Column, bool Ambiguous = false);

/// <summary>
/// One table the model maps, with every entity stored in it (an owner and its owned types, a TPH
/// hierarchy) and the table found in the database; <see cref="Table"/> is null when there is none.
/// </summary>
public sealed record EfTableMatch(
    string Schema,
    string Name,
    IReadOnlyList<EfEntity> Entities,
    TableDetail? Table,
    IReadOnlyList<EfColumnMatch> Columns,
    /// <summary>Several tables differ from the name only in case: none was chosen, none is missing.</summary>
    bool Ambiguous = false)
{
    /// <summary>The entity the table belongs to: the one that shares it with no other.</summary>
    public EfEntity Owner => Entities.FirstOrDefault(e => e.SharesTableWith is null) ?? Entities[0];

    public string Qualified => $"{Schema}.{Name}";
}

public sealed record EfModelMatch(
    IReadOnlyList<EfTableMatch> Tables,
    /// <summary>Tables in the database no entity maps.</summary>
    IReadOnlyList<TableDetail> UnmappedTables,
    /// <summary>Names that matched several objects by case alone: reported, never guessed.</summary>
    IReadOnlyList<string> Ambiguities);

/// <summary>
/// Pairs a model with a database: entity ↔ table, property ↔ column. Pure. A name is matched
/// exactly first, then ignoring case — EF quotes every name, but a naming convention, a case-
/// insensitive collation or a hand-written table can still differ in case. A name that matches
/// several objects by case alone is reported as ambiguous and left unmatched.
/// </summary>
public static class EfModelMatcher
{
    public static EfModelMatch Match(EfModel model, IReadOnlyList<TableDetail> tables, string databaseDefaultSchema)
    {
        List<string> ambiguities = [];
        HashSet<TableDetail> mapped = [];
        List<EfTableMatch> matches = [];

        var groups = model.Entities.GroupBy(
            e => (e.Schema ?? model.DefaultSchema ?? databaseDefaultSchema, e.Table),
            PairComparer.Instance);

        foreach (var group in groups)
        {
            var (schema, name) = group.Key;
            var (table, candidates) = Find(tables, t => (t.Schema, t.Name), (schema, name), $"table {schema}.{name}", ambiguities);
            if (table is not null) mapped.Add(table);
            // Each of the tables it could be is possibly mapped: none is reported as unmapped.
            foreach (var candidate in candidates) mapped.Add(candidate);

            var columns = group
                .SelectMany(entity => entity.Properties.Select(property =>
                {
                    if (table is null) return new EfColumnMatch(entity, property, null);
                    var (column, same) = Find(table.Columns, c => ("", c.Name), ("", property.Column),
                        $"column {schema}.{name}.{property.Column}", ambiguities);
                    return new EfColumnMatch(entity, property, column, same.Count > 0);
                }))
                .ToList();

            matches.Add(new EfTableMatch(schema, name, [.. group], table, columns, candidates.Count > 0));
        }

        return new EfModelMatch(matches, [.. tables.Where(t => !mapped.Contains(t))], ambiguities);
    }

    /// <summary>The entity's table match, found by the entity's own or its table-sharer's name.</summary>
    public static EfTableMatch? TableOf(EfModelMatch match, string entityName) =>
        match.Tables.FirstOrDefault(t => t.Entities.Any(e => e.Name == entityName));

    /// <summary>The columns of <paramref name="properties"/> in <paramref name="table"/>; null when one has no column.</summary>
    public static IReadOnlyList<string>? ColumnsOf(EfTableMatch table, IReadOnlyList<string> properties)
    {
        List<string> columns = [];
        foreach (var property in properties)
        {
            var column = table.Columns.FirstOrDefault(c => c.Property.Name == property);
            if (column?.Column is null) return null;
            columns.Add(column.Column.Name);
        }

        return columns;
    }

    /// <returns>The match, or none and the objects it could be when several differ only in case.</returns>
    private static (T? Match, IReadOnlyList<T> Ambiguous) Find<T>(
        IEnumerable<T> candidates, Func<T, (string, string)> key, (string, string) wanted, string what, List<string> ambiguities)
        where T : class
    {
        var list = candidates as IReadOnlyCollection<T> ?? [.. candidates];

        var exact = list.Where(c => key(c) == wanted).ToList();
        if (exact.Count == 1) return (exact[0], []);

        var byCase = list.Where(c => PairComparer.Instance.Equals(key(c), wanted)).ToList();
        if (byCase.Count == 1) return (byCase[0], []);
        if (byCase.Count == 0) return (null, []);

        ambiguities.Add(
            $"The model's {what} matches {byCase.Count} objects that differ only in case, so it was not compared: " +
            "nothing is reported missing or unmapped for it.");
        return (null, byCase);
    }

    private sealed class PairComparer : IEqualityComparer<(string, string)>
    {
        public static readonly PairComparer Instance = new();

        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string, string) obj) =>
            HashCode.Combine(obj.Item1.ToUpperInvariant(), obj.Item2.ToUpperInvariant());
    }
}
