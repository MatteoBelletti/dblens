using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Denormalization.Rules;

/// <summary>
/// The same non-generic column carried by two FK-linked tables — a value copied down from the
/// parent. Generic names and key columns are excluded: they repeat by design.
/// </summary>
public static class DuplicatedColumnRule
{
    public static IEnumerable<Finding> Apply(IReadOnlyList<TableDetail> tables, DatabaseDialect dialect)
    {
        var byColumn = new Dictionary<(string Name, string Type), List<TableDetail>>(CaseInsensitivePairComparer.Instance);

        foreach (var table in tables)
        {
            var keys = table.KeyColumns(includeReferenced: true);
            foreach (var column in table.Columns)
            {
                if (ColumnNameVocabulary.IsGeneric(column.Name) || keys.Contains(column.Name)) continue;

                if (!byColumn.TryGetValue((column.Name, column.DataType), out var owners))
                    byColumn[(column.Name, column.DataType)] = owners = [];
                owners.Add(table);
            }
        }

        foreach (var ((column, dataType), owners) in byColumn.OrderBy(kv => kv.Key.Name, StringComparer.Ordinal))
        {
            if (owners.Count < 2 || FindLinkedPair(owners) is not { } pair) continue;

            var owned = owners.Select(t => t.Qualified()).Order(StringComparer.Ordinal).ToList();
            var probe = BuildDivergenceQuery(pair, column, dataType, dialect);

            yield return new Finding(
                RuleId: DenormalizationRuleIds.DuplicatedColumn,
                Severity: Severity.Low,
                Category: FindingCategory.Structure,
                Confidence: 0.55,
                Evidence: Evidence.Inferred,
                Table: pair.Child.Qualified(),
                Columns: [column],
                Title: $"Column '{column}' is duplicated across {owned.Count} tables",
                Description:
                    $"'{column}' ({dataType}) appears on: {string.Join(", ", owned)}. " +
                    $"'{pair.Child.Qualified()}' has a foreign key to '{pair.Parent.Qualified()}', so the value " +
                    "can be reached by joining instead of being stored twice, and the two copies drift apart once " +
                    "updated independently. A deliberate point-in-time snapshot (a price captured at order time) " +
                    "is correct as-is — consider renaming it to say so.",
                SqlFix: probe is null
                    ? null
                    : SqlProposal.Probe(probe, "Counts joined rows where the two copies disagree. Returns two numbers and no row values."),
                Metadata: new Dictionary<string, object>
                {
                    ["dataType"] = dataType,
                    ["tables"] = owned,
                    // With three or more owners only one linked pair is measured; the measurement names it.
                    ["comparedChild"] = pair.Child.Qualified(),
                    ["comparedParent"] = pair.Parent.Qualified(),
                });
        }
    }

    /// <summary>The first owner whose outgoing foreign key points at another owner.</summary>
    private static (TableDetail Parent, TableDetail Child, ForeignKeyDetail Fk)? FindLinkedPair(List<TableDetail> owners)
    {
        foreach (var child in owners)
            foreach (var fk in child.ForeignKeysOutgoing)
                if (owners.FirstOrDefault(o => o.Schema == fk.ToSchema && o.Name == fk.ToTable) is { } parent)
                    return (parent, child, fk);

        return null;
    }

    // "text" is the ordinary string type on PostgreSQL but the deprecated LOB type with no equality
    // operator on SQL Server, so one shared list would silently disable most PostgreSQL probes.
    private static readonly HashSet<string> PostgresNonComparableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "xml", "point", "line", "lseg", "box", "path", "polygon", "circle",
    };

    private static readonly HashSet<string> SqlServerNonComparableTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "ntext", "image", "xml", "geography", "geometry", "hierarchyid",
    };

    /// <summary>Counts joined rows where the two copies disagree. Two numbers out, never a row value.</summary>
    internal static string? BuildDivergenceQuery(
        (TableDetail Parent, TableDetail Child, ForeignKeyDetail Fk) pair,
        string columnName,
        string dataType,
        DatabaseDialect dialect)
    {
        if (pair.Fk.FromColumns.Count != 1 || pair.Fk.ToColumns.Count != 1) return null;
        if (pair.Parent.Kind != "table" || pair.Child.Kind != "table") return null;

        var nonComparable = dialect == DatabaseDialect.PostgreSql ? PostgresNonComparableTypes : SqlServerNonComparableTypes;
        if (nonComparable.Contains(dataType.Trim())) return null;

        var child = SqlIdentifier.Qualify(pair.Child.Schema, pair.Child.Name, dialect);
        var parent = SqlIdentifier.Qualify(pair.Parent.Schema, pair.Parent.Name, dialect);
        var col = SqlIdentifier.Quote(columnName, dialect);
        var childKey = SqlIdentifier.Quote(pair.Fk.FromColumns[0], dialect);
        var parentKey = SqlIdentifier.Quote(pair.Fk.ToColumns[0], dialect);

        return dialect == DatabaseDialect.PostgreSql
            ? $"""
               SELECT COUNT(*) AS compared_rows,
                      COUNT(*) FILTER (WHERE c.{col} IS DISTINCT FROM p.{col}) AS divergent_rows
               FROM {child} c
               JOIN {parent} p ON p.{parentKey} = c.{childKey}
               """
            : $"""
               SELECT COUNT_BIG(*) AS compared_rows,
                      COALESCE(SUM(CAST(CASE
                        WHEN c.{col} = p.{col} THEN 0
                        WHEN c.{col} IS NULL AND p.{col} IS NULL THEN 0
                        ELSE 1 END AS BIGINT)), 0) AS divergent_rows
               FROM {child} c
               JOIN {parent} p ON p.{parentKey} = c.{childKey}
               """;
    }
}
