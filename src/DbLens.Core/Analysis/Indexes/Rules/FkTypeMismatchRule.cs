using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Indexes.Rules;

/// <summary>
/// A foreign key column whose type is not the one it references. PostgreSQL accepts it; SQL Server
/// refuses to create one (Msg 1753, 1778), so there it never fires.
/// </summary>
public static class FkTypeMismatchRule
{
    /// <param name="referenced">Every table a foreign key of <paramref name="tables"/> points to, in scope or not.</param>
    public static IEnumerable<Finding> Apply(
        IReadOnlyList<TableDetail> tables, IReadOnlyList<TableDetail> referenced, DatabaseDialect dialect)
    {
        var parents = tables.Concat(referenced).DistinctBy(t => (t.Schema, t.Name)).ToDictionary(t => (t.Schema, t.Name));

        foreach (var table in tables)
        foreach (var fk in table.ForeignKeysOutgoing)
        {
            if (!parents.TryGetValue((fk.ToSchema, fk.ToTable), out var parent)) continue;

            for (var i = 0; i < fk.FromColumns.Count && i < fk.ToColumns.Count; i++)
            {
                if (Column(table, fk.FromColumns[i]) is not { } child || Column(parent, fk.ToColumns[i]) is not { } key) continue;
                if (string.Equals(child.FullDataType, key.FullDataType, StringComparison.OrdinalIgnoreCase)) continue;

                var narrower = IsNarrower(child, key);
                var where = $"{table.Qualified()}.{child.Name}";
                var target = $"{fk.ToSchema}.{fk.ToTable}.{key.Name}";
                yield return new Finding(
                    RuleId:      IndexRuleIds.FkTypeMismatch,
                    Severity:    narrower ? Severity.Medium : Severity.Low,
                    Category:    FindingCategory.Structure,
                    Confidence:  1.0,
                    Evidence:    Evidence.Derived,
                    Table:       table.Qualified(),
                    Columns:     [child.Name],
                    Title:       $"Foreign key column {where} is {child.FullDataType}; it references {target}, {key.FullDataType}",
                    Description: narrower
                        ? $"{where} cannot hold every value of {target}: once the parent's keys outgrow it, inserting a " +
                          "child of those rows fails. Joins on the key also compare across types."
                        : $"Joins on the key compare {child.FullDataType} with {key.FullDataType}, a cast on one side, and the " +
                          "two columns can drift apart further.",
                    SqlFix:      SqlProposal.Ddl(AlterType(table, child, key.FullDataType, dialect),
                        "Changes the column to the referenced type. It rewrites the table under an exclusive lock: run it " +
                        "in a maintenance window."),
                    Metadata:    new Dictionary<string, object>
                    {
                        ["constraint_name"]  = fk.ConstraintName,
                        ["referenced_table"] = $"{fk.ToSchema}.{fk.ToTable}",
                        ["referenced_type"]  = key.FullDataType,
                    });
            }
        }
    }

    private static ColumnDetail? Column(TableDetail table, string name) =>
        table.Columns.FirstOrDefault(c => c.Name == name);

    /// <summary>Same kind of type with less room: a smaller integer, a shorter string, fewer digits.</summary>
    private static bool IsNarrower(ColumnDetail child, ColumnDetail key)
    {
        if (IntegerRank(child.DataType) is { } c && IntegerRank(key.DataType) is { } k) return c < k;
        if (!string.Equals(child.DataType, key.DataType, StringComparison.OrdinalIgnoreCase)) return false;
        return child.CharacterMaxLength < key.CharacterMaxLength
            || (key.CharacterMaxLength is null && child.CharacterMaxLength is not null)
            || child.NumericPrecision - child.NumericScale < key.NumericPrecision - key.NumericScale
            || child.NumericScale < key.NumericScale;
    }

    private static int? IntegerRank(string type) => type.ToLowerInvariant() switch
    {
        "tinyint" => 0,
        "smallint" or "int2" => 1,
        "integer" or "int" or "int4" => 2,
        "bigint" or "int8" => 3,
        _ => null,
    };

    private static string AlterType(TableDetail table, ColumnDetail column, string type, DatabaseDialect dialect)
    {
        var target = SqlIdentifier.Qualify(table.Schema, table.Name, dialect);
        var name = SqlIdentifier.Quote(column.Name, dialect);
        return dialect == DatabaseDialect.PostgreSql
            ? $"ALTER TABLE {target} ALTER COLUMN {name} TYPE {type};"
            : $"ALTER TABLE {target} ALTER COLUMN {name} {type} {(column.IsNullable ? "NULL" : "NOT NULL")};";
    }
}
