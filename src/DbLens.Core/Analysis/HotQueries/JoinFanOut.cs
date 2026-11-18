using DbLens.Core.Analysis.Relationships;
using DbLens.Core.Analysis.Verification;
using DbLens.Core.Models;

namespace DbLens.Core.Analysis.HotQueries;

/// <summary>
/// A query that joins one parent to two 1:N children returns, for each parent, the product of its
/// child rows: what EF Core generates for two Include on collections. Recognized from the query's
/// text and the keys, then — on request — measured with an aggregate probe. Pure.
/// </summary>
public static class JoinFanOut
{
    /// <summary>One parent and the two children the query joins it to, as the catalog names them.</summary>
    public sealed record Shape(
        string QueryId,
        TableDetail Parent,
        TableDetail FirstChild,
        string FirstColumn,
        TableDetail SecondChild,
        string SecondColumn)
    {
        public VerificationTarget Target => new(
            HotQueryRuleIds.JoinFanOut, $"{Parent.Schema}.{Parent.Name}",
            [$"{FirstChild.Schema}.{FirstChild.Name}.{FirstColumn}", $"{SecondChild.Schema}.{SecondChild.Name}.{SecondColumn}"]);
    }

    public static IReadOnlyList<Shape> Detect(
        IReadOnlyList<HotQueryEngine.Ranked> ranked, IReadOnlyList<TableDetail> tables, string defaultSchema, DatabaseDialect dialect)
    {
        List<Shape> shapes = [];

        foreach (var (stats, query, _) in ranked)
        {
            var joins = JoinPredicateExtractor.Extract(
                [new SqlSource(SqlSourceKind.Workload, "statistics", stats.Text)], tables, defaultSchema, dialect);

            // A chain A ← B ← C does not multiply: only two children of the same parent do.
            foreach (var byParent in joins.GroupBy(j => (j.Parent.Schema, j.Parent.Name)))
            {
                var children = byParent
                    .Where(j => !ReferenceEquals(j.Child, j.Parent))
                    .DistinctBy(j => (j.Child.Schema, j.Child.Name, j.ChildColumn))
                    .ToList();
                if (children.Count < 2) continue;

                shapes.Add(new Shape(query.QueryId, children[0].Parent,
                    children[0].Child, children[0].ChildColumn, children[1].Child, children[1].ChildColumn));
            }
        }

        return shapes;
    }

    public static Finding ToSignal(Shape shape)
    {
        var (parent, first, second) = (Name(shape.Parent), Name(shape.FirstChild), Name(shape.SecondChild));

        return new Finding(
            HotQueryRuleIds.JoinFanOut, Severity.Medium, FindingCategory.Performance, 0.7, Evidence.Inferred,
            parent, null,
            $"Query {shape.QueryId} joins {parent} to both {first} and {second}",
            $"Each {parent} row comes back once for every pair of its {first} and {second} rows: 20 of each make 400 " +
            "rows where two separate queries would read 40. EF Core writes this query for two Include on collections. " +
            "It may be deliberate, when a filter leaves one child per parent: verifyJoins measures it on the data.",
            null,
            new Dictionary<string, object>
            {
                ["queryId"] = shape.QueryId,
                ["children"] = new[] { $"{first}.{shape.FirstColumn}", $"{second}.{shape.SecondColumn}" },
                ["ef"] = new Dictionary<string, object>
                {
                    ["fluent"] = ".AsSplitQuery()",
                    ["context"] = "UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)",
                },
            });
    }

    /// <summary>
    /// Two counts: the rows two separate queries read for the children, and the rows the join
    /// returns for the parents that have both. Over the whole tables, not the query's filters.
    /// </summary>
    public static VerificationProbe Probe(Shape shape, IReadOnlyDictionary<(string, string), long?> rowCounts, DatabaseDialect dialect)
    {
        var count = dialect == DatabaseDialect.SqlServer ? "COUNT_BIG(*)" : "COUNT(*)";
        string Table(TableDetail t) => SqlIdentifier.Qualify(t.Schema, t.Name, dialect);
        string Column(string c) => SqlIdentifier.Quote(c, dialect);

        var sql =
            $"SELECT (SELECT {count} FROM {Table(shape.FirstChild)}) + (SELECT {count} FROM {Table(shape.SecondChild)}) AS compared_rows, " +
            "COALESCE(SUM(a.n * b.n), 0) AS divergent_rows " +
            $"FROM (SELECT {Column(shape.FirstColumn)} AS p, {count} AS n FROM {Table(shape.FirstChild)} " +
            $"WHERE {Column(shape.FirstColumn)} IS NOT NULL GROUP BY {Column(shape.FirstColumn)}) a " +
            $"JOIN (SELECT {Column(shape.SecondColumn)} AS p, {count} AS n FROM {Table(shape.SecondChild)} " +
            $"WHERE {Column(shape.SecondColumn)} IS NOT NULL GROUP BY {Column(shape.SecondColumn)}) b ON a.p = b.p";

        var size = Math.Max(
            rowCounts.GetValueOrDefault((shape.FirstChild.Schema, shape.FirstChild.Name)) ?? -1,
            rowCounts.GetValueOrDefault((shape.SecondChild.Schema, shape.SecondChild.Name)) ?? -1);

        return new VerificationProbe(
            shape.Target, VerificationProbeKind.JoinFanOut, sql,
            $"rows the join returns for {Name(shape.Parent)} rows that have both {Name(shape.FirstChild)} and " +
            $"{Name(shape.SecondChild)}, against the rows of the two children read separately",
            size < 0 ? null : size);
    }

    /// <summary>The signal, measured: the counts promote it, the diagnosis stays a hypothesis.</summary>
    public static Finding Apply(Finding signal, VerificationMeasurement m)
    {
        if (m.Status is not (VerificationStatus.Confirmed or VerificationStatus.Refuted))
            return signal with { Metadata = VerificationOutcome.Attach(signal.Metadata, m, null) };

        var separate = m.Denominator!.Value;
        var joined = m.Numerator!.Value;
        var ratio = separate == 0 ? 0 : (double)joined / separate;

        return m.Status == VerificationStatus.Confirmed
            ? signal with
            {
                Evidence = Evidence.Measured,
                Confidence = 1.0,
                Title = $"{signal.Table}: the join returns {Num.N0(joined)} rows where two queries read {Num.N0(separate)}",
                Description =
                    $"MEASURED: over the whole tables, the join returns {Num.N0(joined)} rows for the parents that have " +
                    $"both children; the two children read separately are {Num.N0(separate)} rows ({Num.F1(ratio)}×). " +
                    "Read with a read-only aggregate query; no row values were returned. The query's own filters were " +
                    "not applied, so this is the shape of the data, not tonight's result.\n\n" +
                    "WHAT THIS MAY MEAN: " + signal.Description,
                Metadata = VerificationOutcome.Attach(signal.Metadata, m, ratio),
            }
            : signal with
            {
                Evidence = Evidence.Measured,
                Confidence = 1.0,
                Severity = Severity.Info,
                Title = $"{signal.Table}: the join returns no more rows than two queries ({Num.N0(joined)} against {Num.N0(separate)})",
                Description =
                    $"MEASURED: over the whole tables the join returns {Num.N0(joined)} rows and the two children read " +
                    $"separately are {Num.N0(separate)}: the data has at most one child of one kind per parent, so the " +
                    "join does not multiply.\n\nWHAT WAS SUSPECTED: " + signal.Description,
                Metadata = VerificationOutcome.Attach(signal.Metadata, m, ratio),
            };
    }

    private static string Name(TableDetail t) => $"{t.Schema}.{t.Name}";
}
