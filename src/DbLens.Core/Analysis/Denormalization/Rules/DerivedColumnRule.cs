using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Denormalization.Rules;

/// <summary>
/// Detects columns that cache a value derivable from elsewhere: aggregates of child rows
/// (order_count, total) and concatenations of sibling columns (full_name).
/// Cached values drift from their source unless every write path maintains them.
/// </summary>
public static class DerivedColumnRule
{
    public static IEnumerable<Finding> Apply(IReadOnlyList<TableDetail> tables, DatabaseDialect dialect)
    {
        foreach (var table in tables)
        {
            var tableQualified = table.Qualified();

            // Columns that are already maintained by the database are not a drift risk.
            var generatedColumns = new HashSet<string>(
                table.Columns
                    .Where(c => IsDatabaseGenerated(c))
                    .Select(c => c.Name),
                StringComparer.OrdinalIgnoreCase);

            var keyColumns = table.KeyColumns();

            foreach (var column in table.Columns)
            {
                if (generatedColumns.Contains(column.Name)) continue;
                if (keyColumns.Contains(column.Name)) continue;

                var isAggregate = ColumnNameVocabulary.LooksLikeAggregate(column.Name)
                                  && ColumnNameVocabulary.IsNumericType(column.DataType);

                var isConcatenation = ColumnNameVocabulary.LooksLikeConcatenation(column.Name)
                                      && ColumnNameVocabulary.IsTextType(column.DataType);

                if (!isAggregate && !isConcatenation) continue;

                // An aggregate only makes sense when child rows exist to aggregate over.
                if (isAggregate && table.ForeignKeysIncoming.Count == 0) continue;

                yield return isAggregate
                    ? BuildAggregateFinding(table, tableQualified, column, dialect)
                    : BuildConcatenationFinding(tableQualified, column);
            }
        }
    }

    private static Finding BuildAggregateFinding(
        TableDetail table, string tableQualified, ColumnDetail column, DatabaseDialect dialect)
    {
        var children = table.ForeignKeysIncoming
            .Select(fk => $"{fk.FromSchema}.{fk.FromTable}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        var family = ColumnNameVocabulary.AggregateFamily(column.Name);
        var probe = BuildVerificationQuery(table, column, dialect);
        var resolved = ResolveChild(table, column);

        var metadata = new Dictionary<string, object>
        {
            ["kind"] = "aggregate",
            ["aggregateFamily"] = family.ToString(),
            ["dataType"] = column.FullDataType,
            ["referencingTables"] = children,
        };

        // How the child table was chosen is inferred even when the count is measured, so it is
        // recorded rather than left implicit.
        if (resolved is { } r)
        {
            metadata["childResolution"] = r.Basis;
            metadata["childTable"] = $"{r.Fk.FromSchema}.{r.Fk.FromTable}";
        }

        var whyNotCheckable = family == ColumnNameVocabulary.AggregateKind.SumLike
            ? " This one cannot be checked against the data: the column being summed or averaged " +
              "is not recorded anywhere in the catalog, so there is nothing to compare against."
            : probe is null
                ? " This one cannot be checked against the data: the parent key or the child table " +
                  "could not be established without guessing."
                : "";

        return new Finding(
            RuleId:     DenormalizationRuleIds.DerivedColumn,
            Severity:   Severity.Medium,
            Category:   FindingCategory.Structure,
            Confidence: 0.6,
            Evidence: Evidence.Inferred,
            Table:      tableQualified,
            Columns:    [column.Name],
            Title:      $"Column '{column.Name}' on '{tableQualified}' looks like a cached aggregate",
            Description:
                $"'{column.Name}' ({column.FullDataType}) is named like an aggregate over child rows. " +
                $"Tables referencing {tableQualified}: {string.Join(", ", children)}. " +
                "A stored aggregate silently drifts from its source unless every insert, update and delete " +
                "path maintains it — including bulk loads and manual fixes. " +
                "Consider computing it on read, or maintaining it in a generated/computed column or trigger " +
                "so the database enforces consistency. " +
                "If this column is a deliberate point-in-time snapshot rather than a live aggregate, it is correct as-is." +
                whyNotCheckable,
            SqlFix:     probe is null
                ? null
                : SqlProposal.Probe(
                    probe,
                    "Counts parent rows whose stored value disagrees with a live count of child rows. " +
                    "Returns two numbers and no row values."),
            Metadata:   metadata);
    }

    private static Finding BuildConcatenationFinding(string tableQualified, ColumnDetail column)
    {
        return new Finding(
            RuleId:     DenormalizationRuleIds.DerivedColumn,
            Severity:   Severity.Low,
            Category:   FindingCategory.Structure,
            Confidence: 0.7,
            Evidence: Evidence.Inferred,
            Table:      tableQualified,
            Columns:    [column.Name],
            Title:      $"Column '{column.Name}' on '{tableQualified}' looks like a concatenation of sibling columns",
            Description:
                $"'{column.Name}' ({column.FullDataType}) is named like a value composed from other columns " +
                "in the same row. Storing it duplicates data that already exists and can fall out of sync " +
                "when the source columns change. " +
                "Consider a generated/computed column, or composing the value in the application or a view.",
            SqlFix:     null,
            Metadata:   new Dictionary<string, object>
            {
                ["kind"] = "concatenation",
                ["dataType"] = column.FullDataType,
            });
    }

    /// <summary>
    /// The child table whose rows the cached count is supposed to count, and how that was decided.
    /// Null when it cannot be established without guessing.
    /// </summary>
    internal static (ForeignKeyDetail Fk, string Basis)? ResolveChild(
        TableDetail table, ColumnDetail column)
    {
        var candidates = table.ForeignKeysIncoming
            .Where(fk => fk.FromColumns.Count == 1 && fk.ToColumns.Count == 1)
            .ToList();

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return (candidates[0], "sole-fk");

        // Several children: accept only when exactly one child table name matches the column's
        // stem (order_count -> orders). Picking the first, as this rule used to, is a coin flip,
        // and a measurement against the wrong table is worse than no measurement.
        var stem = StripAggregateAffixes(column.Name);
        if (stem.Length < 3) return null;

        var byName = candidates
            .Where(fk => NameMatchesStem(fk.FromTable, stem))
            .ToList();

        return byName.Count == 1 ? (byName[0], "name-match") : null;
    }

    private static string StripAggregateAffixes(string columnName)
    {
        foreach (var suffix in ColumnNameVocabulary.AggregateSuffixes)
            if (columnName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return columnName[..^suffix.Length];

        foreach (var prefix in ColumnNameVocabulary.AggregatePrefixes)
            if (columnName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return columnName[prefix.Length..];

        return columnName;
    }

    private static bool NameMatchesStem(string tableName, string stem) =>
        string.Equals(tableName, stem, StringComparison.OrdinalIgnoreCase)
        || string.Equals(tableName, stem + "s", StringComparison.OrdinalIgnoreCase)
        || string.Equals(tableName + "s", stem, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Counts parent rows whose stored value disagrees with a live count of child rows.
    ///
    /// Aggregates only: the query returns two numbers and never a row value, so verification
    /// cannot leak data the tool promises not to read. Returns null whenever the comparison
    /// would require a guess — a missing probe is honest, a wrong measurement is not.
    /// </summary>
    internal static string? BuildVerificationQuery(
        TableDetail table, ColumnDetail column, DatabaseDialect dialect)
    {
        // Only a count can be checked. A sum or average needs the source column being
        // aggregated, which nothing in the catalog records.
        if (ColumnNameVocabulary.AggregateFamily(column.Name)
            != ColumnNameVocabulary.AggregateKind.CountLike) return null;

        if (table.Kind != "table") return null;
        if (table.PrimaryKey is not { Columns.Count: 1 }) return null;
        if (ResolveChild(table, column) is not { } resolved) return null;

        var fk = resolved.Fk;
        var pk = table.PrimaryKey.Columns[0];

        // Grouping by a non-unique parent column would silently merge parents together.
        if (!string.Equals(fk.ToColumns[0], pk, StringComparison.OrdinalIgnoreCase)
            && !table.UniqueConstraints.Any(u =>
                u.Columns.Count == 1
                && string.Equals(u.Columns[0], fk.ToColumns[0], StringComparison.OrdinalIgnoreCase)))
            return null;

        var parent = SqlIdentifier.Qualify(table.Schema, table.Name, dialect);
        var child = SqlIdentifier.Qualify(fk.FromSchema, fk.FromTable, dialect);
        var stored = SqlIdentifier.Quote(column.Name, dialect);
        var parentKey = SqlIdentifier.Quote(pk, dialect);
        var childKey = SqlIdentifier.Quote(fk.FromColumns[0], dialect);

        // COUNT(c.<fk>) rather than COUNT(*): under a LEFT JOIN, COUNT(*) returns 1 for a
        // parent with no children at all.
        return dialect == DatabaseDialect.PostgreSql
            ? $"""
               SELECT COUNT(*) AS compared_rows,
                      COUNT(*) FILTER (WHERE t.stored_value IS DISTINCT FROM t.actual_value) AS divergent_rows
               FROM (
                 SELECT p.{stored} AS stored_value, COUNT(c.{childKey}) AS actual_value
                 FROM {parent} p
                 LEFT JOIN {child} c ON c.{childKey} = p.{parentKey}
                 GROUP BY p.{parentKey}, p.{stored}
               ) t
               """
            : $"""
               SELECT COUNT_BIG(*) AS compared_rows,
                      COALESCE(SUM(CAST(CASE WHEN t.stored_value = t.actual_value THEN 0 ELSE 1 END AS BIGINT)), 0) AS divergent_rows
               FROM (
                 SELECT p.{stored} AS stored_value, COUNT(c.{childKey}) AS actual_value
                 FROM {parent} p
                 LEFT JOIN {child} c ON c.{childKey} = p.{parentKey}
                 GROUP BY p.{parentKey}, p.{stored}
               ) t
               """;
    }

    private static bool IsDatabaseGenerated(ColumnDetail column) =>
        column.IsIdentity
        || column.IsGenerated
        || (column.DefaultExpression is not null
            && column.DefaultExpression.Contains("generated", StringComparison.OrdinalIgnoreCase));

}
