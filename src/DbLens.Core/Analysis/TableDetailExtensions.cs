using DbLens.Core.Models;

namespace DbLens.Core.Analysis;

/// <summary>Small structural queries over a table that several rules need.</summary>
public static class TableDetailExtensions
{
    /// <summary>"schema.table", the form every finding's Table field uses.</summary>
    public static string Qualified(this TableDetail table) => $"{table.Schema}.{table.Name}";

    /// <summary>
    /// Columns that take part in a key: the primary key and the referencing side of outgoing
    /// foreign keys. Key columns repeat across tables by design, so the naming heuristics skip them.
    /// </summary>
    /// <param name="includeReferenced">
    /// Also include columns other tables' foreign keys point at — the referenced side.
    /// </param>
    public static HashSet<string> KeyColumns(this TableDetail table, bool includeReferenced = false)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (table.PrimaryKey is not null)
            keys.UnionWith(table.PrimaryKey.Columns);

        foreach (var fk in table.ForeignKeysOutgoing)
            keys.UnionWith(fk.FromColumns);

        if (includeReferenced)
            foreach (var fk in table.ForeignKeysIncoming)
                keys.UnionWith(fk.ToColumns);

        return keys;
    }
}

/// <summary>
/// Case-insensitive equality for a pair of identifiers, such as a column name and its type.
/// Element names on the tuple are irrelevant to the type, so one comparer serves every pair.
/// </summary>
public static class IndexDetailExtensions
{
    /// <summary>
    /// Keeps its key in order (a b-tree; SQL Server's rowstore indexes), so a leading part of the
    /// key can be searched on its own. A hash, GIN or columnstore index cannot.
    /// </summary>
    public static bool IsOrdered(this IndexDetail index) =>
        index.Method.Equals("btree", StringComparison.OrdinalIgnoreCase)
        || index.Method.Equals("CLUSTERED", StringComparison.OrdinalIgnoreCase)
        || index.Method.Equals("NONCLUSTERED", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Included(this IndexDetail index) => index.IncludedColumns ?? [];

    public static bool StartsWith(this IndexDetail index, IReadOnlyList<string> columns)
    {
        if (index.Columns.Count < columns.Count) return false;

        for (var i = 0; i < columns.Count; i++)
            if (!string.Equals(index.Columns[i], columns[i], StringComparison.OrdinalIgnoreCase))
                return false;

        return true;
    }
}

public sealed class CaseInsensitivePairComparer : IEqualityComparer<(string, string)>
{
    public static readonly CaseInsensitivePairComparer Instance = new();

    public bool Equals((string, string) x, (string, string) y) =>
        string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase)
        && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);

    public int GetHashCode((string, string) obj) =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
}
