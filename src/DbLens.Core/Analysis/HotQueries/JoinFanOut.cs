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

        // Every branch of every query in one extraction, named "<query>/<branch>": the catalog lookups are built once.
        List<SqlSource> branches = [.. ranked.SelectMany((r, q) => OuterBranches(r.Stats.Text, dialect)
            .Select((text, b) => new SqlSource(SqlSourceKind.Workload, $"{q}/{b}", text)))];
        var joins = JoinPredicateExtractor.Extract(branches, tables, defaultSchema, dialect);

        // A chain A ← B ← C does not multiply, nor one child joined through two keys (billing and
        // shipping address): only two child tables of the same parent key, in one branch, do.
        foreach (var byParent in joins.GroupBy(j => (Branch: j.Source.Name, j.Parent.Schema, j.Parent.Name, j.ParentKey)))
        {
            var children = byParent
                .DistinctBy(j => (j.Child.Schema, j.Child.Name))
                // In a fixed order: the same pair joined the other way round is the same shape.
                .OrderBy(j => j.Child.Schema, StringComparer.Ordinal).ThenBy(j => j.Child.Name, StringComparer.Ordinal)
                .ToList();
            if (children.Count < 2) continue;

            var query = ranked[int.Parse(byParent.Key.Branch.Split('/')[0], System.Globalization.CultureInfo.InvariantCulture)].Query;
            shapes.Add(new Shape(query.QueryId, children[0].Parent,
                children[0].Child, children[0].ChildColumn, children[1].Child, children[1].ChildColumn));
        }

        return shapes;
    }

    private static readonly HashSet<string> SetOperators = new(["UNION", "EXCEPT", "INTERSECT"], StringComparer.OrdinalIgnoreCase);

    // A subquery after these is a source of the query's rows: a derived table, a CTE body, a branch.
    private static readonly HashSet<string> RowSources = new(
        ["FROM", "JOIN", "APPLY", "LATERAL", "AS", "MATERIALIZED", "UNION", "EXCEPT", "INTERSECT"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The joins that shape the rows a query returns: each top-level branch of a UNION, EXCEPT or
    /// INTERSECT on its own, without the subqueries of its conditions (EXISTS, IN, a scalar subquery,
    /// however parenthesized), which never multiply them. A derived table, a CTE body and a
    /// parenthesized branch stay, with only the first branch of a set operation nested in them.
    /// </summary>
    public static IReadOnlyList<string> OuterBranches(string sql, DatabaseDialect dialect)
    {
        var tokens = SqlText.Tokens(sql, dialect).ToList();
        List<string> branches = [];
        var current = new System.Text.StringBuilder();
        // For each open parenthesis, whether what it holds is rows of the query; the text is a row source.
        Stack<bool> rows = [];
        var (skipFrom, previous) = (-1, "");

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var inRows = rows.Count == 0 || rows.Peek();

            if (token.Kind == SqlTokenKind.Symbol && token.Text == "(")
            {
                var holdsRows = previous == "" || RowSources.Contains(previous) || (previous == "(" && inRows);
                rows.Push(holdsRows);
                if (skipFrom < 0 && !holdsRows && StartsSubquery(tokens, i + 1)) skipFrom = rows.Count;
            }
            else if (token.Kind == SqlTokenKind.Symbol && token.Text == ")" && rows.Count > 0)
            {
                var closing = rows.Count;
                rows.Pop();
                if (skipFrom == closing)
                {
                    skipFrom = -1;
                    current.Append(' ');
                    continue;
                }
            }

            if (skipFrom >= 0) continue;

            if (token.Kind == SqlTokenKind.Word && SetOperators.Contains(token.Text))
            {
                // Inside a derived table or CTE body only its first branch stays with the outer query,
                // whose aliases the later joins need; at the top each branch is read on its own.
                if (rows.Count > 0) { skipFrom = rows.Count; continue; }
                branches.Add(current.ToString());
                current.Clear();
            }
            else current.Append(token.Source);

            // UNION ALL (SELECT …): the operand follows ALL and is still a branch; x > ALL (SELECT …) is not.
            var afterSetOperator = SetOperators.Contains(previous) && token.Text.Equals("ALL", StringComparison.OrdinalIgnoreCase);
            if (token.Kind is SqlTokenKind.Word or SqlTokenKind.Symbol && !afterSetOperator) previous = token.Text;
        }

        branches.Add(current.ToString());
        return branches;
    }

    private static bool StartsSubquery(List<SqlToken> tokens, int from)
    {
        while (from < tokens.Count && tokens[from].Kind is SqlTokenKind.Space or SqlTokenKind.Comment) from++;
        return from < tokens.Count && tokens[from].Kind == SqlTokenKind.Word
               && (tokens[from].Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
                   || tokens[from].Text.Equals("WITH", StringComparison.OrdinalIgnoreCase));
    }

    public static Finding ToSignal(Shape shape)
    {
        var (parent, first, second) = (shape.Parent.Qualified(), shape.FirstChild.Qualified(), shape.SecondChild.Qualified());

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
        var count = SqlCount.Of(dialect);
        string Table(TableDetail t) => SqlIdentifier.Qualify(t.Schema, t.Name, dialect);
        string Column(string c) => SqlIdentifier.Quote(c, dialect);

        var sql =
            $"SELECT (SELECT {count} FROM {Table(shape.FirstChild)}) + (SELECT {count} FROM {Table(shape.SecondChild)}) AS compared_rows, " +
            "COALESCE(SUM(a.n * b.n), 0) AS divergent_rows " +
            $"FROM (SELECT {Column(shape.FirstColumn)} AS p, {count} AS n FROM {Table(shape.FirstChild)} " +
            $"WHERE {Column(shape.FirstColumn)} IS NOT NULL GROUP BY {Column(shape.FirstColumn)}) a " +
            $"JOIN (SELECT {Column(shape.SecondColumn)} AS p, {count} AS n FROM {Table(shape.SecondChild)} " +
            $"WHERE {Column(shape.SecondColumn)} IS NOT NULL GROUP BY {Column(shape.SecondColumn)}) b ON a.p = b.p";

        // Unknown when either child is: the budget's size limits must hold for both tables scanned.
        var (first, second) = (rowCounts.GetValueOrDefault((shape.FirstChild.Schema, shape.FirstChild.Name)),
            rowCounts.GetValueOrDefault((shape.SecondChild.Schema, shape.SecondChild.Name)));
        long? size = first is { } a && second is { } b ? Math.Max(a, b) : null;

        return new VerificationProbe(
            shape.Target, VerificationProbeKind.JoinFanOut, sql,
            $"rows the join returns for {shape.Parent.Qualified()} rows that have both {shape.FirstChild.Qualified()} and " +
            $"{shape.SecondChild.Qualified()}, against the rows of the two children read separately",
            size);
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
                    $"separately are {Num.N0(separate)}: in total the join does not multiply. A total can still hide a few " +
                    "parents with many children of both kinds.\n\nWHAT WAS SUSPECTED: " + signal.Description,
                Metadata = VerificationOutcome.Attach(signal.Metadata, m, ratio),
            };
    }

}
