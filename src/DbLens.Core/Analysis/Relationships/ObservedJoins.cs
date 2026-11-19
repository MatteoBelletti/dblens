using System.Text.RegularExpressions;
using DbLens.Core.Models;
using static DbLens.Core.Analysis.Names;

namespace DbLens.Core.Analysis.Relationships;

/// <summary>SQL the database already holds: a view, a routine, or a statement from the workload history.</summary>
public sealed record SqlSource(SqlSourceKind Kind, string Name, string Sql, long? Executions = null);

public enum SqlSourceKind { View, Routine, Workload }

/// <summary>Everything a dialect could read, plus why some of it could not be read.</summary>
public sealed record SqlSourceSet(IReadOnlyList<SqlSource> Sources, IReadOnlyList<string> Caveats)
{
    public static readonly SqlSourceSet Empty = new([], []);
}

/// <summary>An equality join between a column and a single-column key, seen in some SQL source.</summary>
public sealed record ObservedJoin(
    TableDetail Child, string ChildColumn, TableDetail Parent, string ParentKey, SqlSource Source)
{
    public string Describe() => Source.Kind switch
    {
        SqlSourceKind.Workload => $"workload ({Source.Name}{(Source.Executions is { } n ? $", {Num.N0(n)} executions" : "")})",
        SqlSourceKind.View => $"view {Source.Name}",
        _ => $"routine {Source.Name}",
    };
}

/// <summary>
/// Finds <c>a.x = b.y</c> joins in SQL text and resolves them against the catalog. Not a parser:
/// aliases come from FROM / JOIN / comma lists, and a pair is kept only when both sides resolve to
/// real columns and exactly one side is a single-column key, which says which way the reference points.
/// Literal values are blanked first and no SQL text leaves this class, so a workload query holding
/// personal data in a WHERE clause contributes table and column names only.
/// </summary>
public static class JoinPredicateExtractor
{
    private const string Name = @"(?:[A-Za-z_][\w$]*\.){0,2}[A-Za-z_][\w$]*";

    // The alias may not be a keyword: in "SELECT a.x, a.y FROM t" the comma branch would otherwise
    // read FROM as the alias of "a.y" and consume it, hiding the table that follows.
    private const string Keyword =
        "(?:FROM|JOIN|ON|WHERE|INNER|LEFT|RIGHT|FULL|CROSS|OUTER|GROUP|ORDER|USING|UNION|LIMIT|HAVING|NATURAL|LATERAL|WITH|SET|SELECT|AS)";

    private static readonly Regex TableRef = new(
        $@"(?:\bFROM|\bJOIN|,)\s*\(*\s*(?<name>{Name})(?:\s+(?:AS\s+)?(?!{Keyword}\b)(?<alias>[A-Za-z_]\w*))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Equality = new(
        @"\b(?<a>[A-Za-z_]\w*)\.(?<ac>[A-Za-z_]\w*)\s*=\s*(?<b>[A-Za-z_]\w*)\.(?<bc>[A-Za-z_]\w*)\b",
        RegexOptions.Compiled);

    public static IReadOnlyList<ObservedJoin> Extract(
        IEnumerable<SqlSource> sources, IReadOnlyList<TableDetail> tables, string defaultSchema, DatabaseDialect dialect)
    {
        // SQL text names tables without regard to case, so the lookup does too. Names that differ
        // only in case (legal on PostgreSQL and on a case-sensitive SQL Server) cannot be told
        // apart from the text and are left unresolved rather than guessed.
        var byQualified = tables.GroupBy(t => $"{t.Schema}.{t.Name}", StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byName = tables.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        TableDetail? Resolve(string name)
        {
            var parts = name.Split('.');
            if (parts.Length >= 2)
                return byQualified.GetValueOrDefault($"{parts[^2]}.{parts[^1]}");
            if (byQualified.GetValueOrDefault($"{defaultSchema}.{name}") is { } inDefault) return inDefault;
            return byName.TryGetValue(name, out var list) && list.Count == 1 ? list[0] : null;
        }

        List<ObservedJoin> joins = [];

        foreach (var source in sources)
        {
            // Literals are blanked and quoted names unquoted, so the patterns see bare identifiers only.
            var sql = SqlText.Rewrite(
                SqlText.Segments(source.Sql, dialect),
                s => s.Kind == SqlSegmentKind.StringLiteral ? "''" : s.Text);
            var aliases = new Dictionary<string, TableDetail>(StringComparer.OrdinalIgnoreCase);

            foreach (Match m in TableRef.Matches(sql))
            {
                if (Resolve(m.Groups["name"].Value) is not { } table) continue;
                aliases[table.Name] = table;
                var alias = m.Groups["alias"].Value;
                if (alias.Length > 0) aliases[alias] = table;
            }

            foreach (Match m in Equality.Matches(sql))
            {
                if (!aliases.TryGetValue(m.Groups["a"].Value, out var left)
                    || !aliases.TryGetValue(m.Groups["b"].Value, out var right)
                    || ReferenceEquals(left, right))
                    continue;

                var leftCol = ColumnOf(left, m.Groups["ac"].Value);
                var rightCol = ColumnOf(right, m.Groups["bc"].Value);
                if (leftCol is null || rightCol is null) continue;

                var leftIsKey = IsSingleKey(left, leftCol);
                var rightIsKey = IsSingleKey(right, rightCol);
                if (leftIsKey == rightIsKey) continue;

                joins.Add(leftIsKey
                    ? new ObservedJoin(right, rightCol, left, leftCol, source)
                    : new ObservedJoin(left, leftCol, right, rightCol, source));
            }
        }

        return joins;
    }

    private static string? ColumnOf(TableDetail table, string name) =>
        table.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Name;

    private static bool IsSingleKey(TableDetail table, string column) =>
        (table.PrimaryKey is { Columns.Count: 1 } pk && Same(pk.Columns[0], column))
        || table.UniqueConstraints.Any(u => u.Columns.Count == 1 && Same(u.Columns[0], column));
}
