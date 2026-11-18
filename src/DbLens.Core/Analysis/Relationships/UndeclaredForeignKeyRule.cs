using DbLens.Core.Analysis.Denormalization;
using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Relationships;

/// <summary>
/// Suggests foreign keys the schema does not declare, from joins the database's own SQL performs
/// (views, routines, workload history) and, failing that, from names and types. Every suggestion
/// is a signal: only the verification pass turns one into a measurement.
/// </summary>
public static class UndeclaredForeignKeyRule
{
    /// <summary>A suggested reference, with the referenced key resolved.</summary>
    public sealed record Candidate(
        TableDetail Child,
        ColumnDetail Column,
        TableDetail Parent,
        string ParentKey,
        string Basis,
        double Confidence,
        IReadOnlyList<string> SeenIn);

    public sealed record Outcome(IReadOnlyList<Candidate> Candidates, int AmbiguousSkipped);

    public static Outcome Find(IReadOnlyList<TableDetail> tables, IReadOnlyList<ObservedJoin>? observed = null)
    {
        var joins = (observed ?? [])
            .ToLookup(j => (j.Child, j.ChildColumn.ToLowerInvariant()));

        List<Candidate> candidates = [];
        var ambiguous = 0;

        foreach (var child in tables.Where(t => t.Kind == "table"))
        {
            var declared = new HashSet<string>(
                child.ForeignKeysOutgoing.SelectMany(fk => fk.FromColumns), StringComparer.OrdinalIgnoreCase);
            var primaryKey = new HashSet<string>(
                child.PrimaryKey?.Columns ?? [], StringComparer.OrdinalIgnoreCase);

            foreach (var column in child.Columns)
            {
                if (declared.Contains(column.Name) || primaryKey.Contains(column.Name)) continue;

                var matches = MatchParents(child, column, tables).ToList();
                var seen = joins[(child, column.Name.ToLowerInvariant())]
                    .Where(j => Compatible(column, j.Parent, j.ParentKey))
                    .GroupBy(j => (j.Parent, j.ParentKey.ToLowerInvariant()))
                    .ToList();

                // What the database's own SQL joins on beats what a name suggests, and settles a
                // name that matched several parents.
                if (seen.Count == 1)
                {
                    var target = seen[0].First();
                    var alsoNamed = matches.Any(c => ReferenceEquals(c.Parent, target.Parent));
                    candidates.Add(new Candidate(
                        child, column, target.Parent, target.ParentKey, "observed-join", alsoNamed ? 0.85 : 0.8,
                        [.. seen[0].Select(j => j.Describe()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]));
                }
                else if (seen.Count > 1 || matches.Count > 1) ambiguous++;
                else if (matches.Count == 1) candidates.Add(matches[0]);
            }
        }

        return new Outcome(candidates, ambiguous);
    }

    private static IEnumerable<Candidate> MatchParents(
        TableDetail child, ColumnDetail column, IReadOnlyList<TableDetail> tables)
    {
        var stem = ReferenceNaming.StemOf(column.Name);

        foreach (var parent in tables.Where(t => t.Kind == "table" && !ReferenceEquals(t, child)))
        {
            // Strongest form: the column carries the parent's own key name (customer_id -> customers.customer_id).
            if (SingleKey(parent) is { } pk
                && string.Equals(pk, column.Name, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(pk, "id", StringComparison.OrdinalIgnoreCase)
                && Compatible(column, parent, pk))
            {
                yield return new Candidate(child, column, parent, pk, "same-name-as-parent-key", 0.7, []);
                continue;
            }

            if (stem is null || !ReferenceNaming.TableMatchesStem(parent.Name, stem.Stem)) continue;

            var key = stem.Family == ReferenceNaming.KeyFamily.Id ? SingleKey(parent) : CodeKey(parent);
            if (key is null || !Compatible(column, parent, key)) continue;

            yield return stem.Family == ReferenceNaming.KeyFamily.Id
                ? new Candidate(child, column, parent, key, "id-name-matches-table", 0.55, [])
                : new Candidate(child, column, parent, key, "code-name-matches-table", 0.45, []);
        }
    }

    /// <summary>The parent's single-column primary key, the only key a surrogate reference can target.</summary>
    private static string? SingleKey(TableDetail table) =>
        table.PrimaryKey is { Columns.Count: 1 } pk ? pk.Columns[0] : null;

    /// <summary>
    /// A single-column unique key whose name looks like a code. Business codes are often unique
    /// but not the primary key, which is exactly why ERPs leave the reference undeclared.
    /// </summary>
    private static string? CodeKey(TableDetail table)
    {
        var keys = table.UniqueConstraints.Where(u => u.Columns.Count == 1).Select(u => u.Columns[0])
            .Concat(table.PrimaryKey is { Columns.Count: 1 } pk ? [pk.Columns[0]] : []);

        return keys.FirstOrDefault(k => k.StartsWith("cod", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A join only makes sense between values of the same family: numbers with numbers, text with text.</summary>
    private static bool Compatible(ColumnDetail column, TableDetail parent, string parentKey)
    {
        var target = parent.Columns.FirstOrDefault(c => string.Equals(c.Name, parentKey, StringComparison.OrdinalIgnoreCase));
        if (target is null) return false;

        return (ColumnNameVocabulary.IsNumericType(column.DataType) && ColumnNameVocabulary.IsNumericType(target.DataType))
            || (ColumnNameVocabulary.IsTextType(column.DataType) && ColumnNameVocabulary.IsTextType(target.DataType));
    }

    public static Finding ToSignal(Candidate c) =>
        new(
            RuleId: RelationshipRuleIds.UndeclaredForeignKey,
            Severity: Severity.Low,
            Category: FindingCategory.Structure,
            Confidence: c.Confidence,
            Evidence: Evidence.Inferred,
            Table: c.Child.Qualified(),
            Columns: [c.Column.Name],
            Title: $"'{c.Child.Qualified()}.{c.Column.Name}' looks like an undeclared reference to '{c.Parent.Qualified()}.{c.ParentKey}'",
            Description:
                (c.SeenIn.Count > 0
                    ? $"No foreign key is declared on '{c.Column.Name}', but SQL in the database joins it to the key of " +
                      $"'{c.Parent.Qualified()}': {string.Join("; ", c.SeenIn)}. "
                    : $"No foreign key is declared on '{c.Column.Name}', but its name and type match the key of " +
                      $"'{c.Parent.Qualified()}' (basis: {c.Basis}). ") +
                "If it is a reference, the database is not enforcing it: rows can point at parents that do not " +
                "exist, deletes do not propagate, and the optimizer cannot use the relationship. Verify it " +
                "against the data before relying on it as a join path.",
            SqlFix: null,
            Metadata: new Dictionary<string, object>
            {
                ["referencedTable"] = c.Parent.Qualified(),
                ["referencedColumn"] = c.ParentKey,
                ["basis"] = c.Basis,
                ["seenIn"] = c.SeenIn,
            });

    /// <summary>
    /// Counts non-null referencing values that match no parent row. The parent key is unique by
    /// construction, so the LEFT JOIN cannot multiply rows. Aggregates only: two numbers out.
    /// </summary>
    public static string? BuildInclusionProbe(Candidate c, DatabaseDialect dialect)
    {
        var child = SqlIdentifier.Qualify(c.Child.Schema, c.Child.Name, dialect);
        var parent = SqlIdentifier.Qualify(c.Parent.Schema, c.Parent.Name, dialect);
        var col = SqlIdentifier.Quote(c.Column.Name, dialect);
        var key = SqlIdentifier.Quote(c.ParentKey, dialect);

        return $"""
                SELECT {SqlCount.Of(dialect, $"c.{col}")} AS compared_rows,
                       {SqlCount.Of(dialect, $"c.{col}")} - {SqlCount.Of(dialect, $"p.{key}")} AS divergent_rows
                FROM {child} c
                LEFT JOIN {parent} p ON p.{key} = c.{col}
                """;
    }

    /// <summary>The constraint that would enforce a verified reference.</summary>
    public static SqlProposal BuildConstraint(Candidate c, DatabaseDialect dialect)
    {
        var name = SqlIdentifier.Quote($"fk_{c.Child.Name}_{c.Column.Name}", dialect);
        var child = SqlIdentifier.Qualify(c.Child.Schema, c.Child.Name, dialect);
        var parent = SqlIdentifier.Qualify(c.Parent.Schema, c.Parent.Name, dialect);
        var col = SqlIdentifier.Quote(c.Column.Name, dialect);
        var key = SqlIdentifier.Quote(c.ParentKey, dialect);

        // NOT VALID takes only a brief lock; VALIDATE then scans without blocking writes.
        var sql = dialect == DatabaseDialect.PostgreSql
            ? $"ALTER TABLE {child} ADD CONSTRAINT {name} FOREIGN KEY ({col}) REFERENCES {parent} ({key}) NOT VALID;\n" +
              $"ALTER TABLE {child} VALIDATE CONSTRAINT {name};"
            : $"ALTER TABLE {child} WITH CHECK ADD CONSTRAINT {name} FOREIGN KEY ({col}) REFERENCES {parent} ({key});";

        return SqlProposal.Ddl(sql, "Declares the foreign key so the database enforces the reference.");
    }
}
