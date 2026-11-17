using DbLens.Core.Analysis.Verification;
using DbLens.Core.Models;

namespace DbLens.Core.Analysis.HotQueries;

/// <summary>
/// The heaviest queries the engine has counted, and what their statistics alone establish. Pure.
/// Every figure is the engine's own measurement; the text is returned only redacted.
/// </summary>
public static class HotQueryEngine
{
    private const int MaxTextLength = 2_000;

    /// <summary>A listed query with the statistics it came from.</summary>
    public sealed record Ranked(HotQueryStats Stats, HotQuery Query, IReadOnlyList<(string Schema, string Name)> Tables);

    public static readonly IReadOnlyList<string> AllRuleIds = [.. HotQueryRuleIds.All, .. QueryPlanRuleIds.All];

    /// <summary>
    /// The heaviest queries by total time that name at least one user table. dblens's own probes
    /// and catalog-only queries are set aside, so the list is the application's workload.
    /// </summary>
    public static IReadOnlyList<Ranked> Rank(
        IReadOnlyList<HotQueryStats> stats,
        IReadOnlyList<(string Schema, string Name)> userTables,
        DatabaseDialect dialect,
        FindHotQueriesRequest request) =>
        [.. stats
            .Where(s => !s.Text.Contains("divergent_rows", StringComparison.OrdinalIgnoreCase))
            .Select(s => (Stats: s, Redacted: SqlText.Redact(s.Text, dialect)))
            .Where(x => IsDataStatement(x.Redacted))
            .Select(x => (x.Stats, x.Redacted, Tables: TablesNamed(x.Stats.Text, userTables, dialect)))
            .Where(x => x.Tables.Count > 0)
            .OrderByDescending(x => x.Stats.TotalTimeMs)
            .Take(request.Top)
            .Select(x => new Ranked(x.Stats, Describe(x.Stats, x.Redacted, x.Tables), x.Tables))];

    private static readonly string[] DataStatements = ["SELECT", "WITH", "INSERT", "UPDATE", "DELETE", "MERGE"];

    /// <summary>
    /// Whether the statement reads or writes rows: DDL, transaction control and utility statements
    /// are counted by the engine too, but are not workload. Query Store prefixes a parameterized
    /// query with its parameter list, "(@p0 int)SELECT …", which is skipped.
    /// </summary>
    public static bool IsDataStatement(string redacted)
    {
        var text = redacted.TrimStart();
        if (text.StartsWith("(@", StringComparison.Ordinal))
        {
            // The list holds types with their own parentheses, nvarchar(4000): find the one that closes it.
            var depth = 0;
            for (var i = 0; i < text.Length; i++)
            {
                depth += text[i] switch { '(' => 1, ')' => -1, _ => 0 };
                if (depth != 0) continue;
                text = text[(i + 1)..].TrimStart();
                break;
            }
        }
        while (text.StartsWith('(')) text = text[1..].TrimStart();

        var end = 0;
        while (end < text.Length && char.IsLetter(text[end])) end++;
        return DataStatements.Contains(text[..end], StringComparer.OrdinalIgnoreCase);
    }

    private static HotQuery Describe(HotQueryStats s, string redacted, IReadOnlyList<(string Schema, string Name)> tables)
    {
        var text = CollapseSpaces(redacted);
        if (text.Length > MaxTextLength) text = text[..MaxTextLength] + "…";
        var calls = Math.Max(s.Calls, 1);

        return new HotQuery(
            s.QueryId, text, s.Calls,
            Math.Round(s.TotalTimeMs, 1),
            Math.Round(s.TotalTimeMs / calls, 2),
            Math.Round((double)s.Rows / calls, 1),
            s.Rows > 0 ? Math.Round((double)s.PagesRead / s.Rows, 1) : null,
            s.PagesRead > 0 ? Math.Round((double)s.PagesFromDisk / s.PagesRead, 3) : 0,
            s.TempPages,
            [.. tables.Select(t => $"{t.Schema}.{t.Name}")]);
    }

    /// <summary>What the statistics alone establish about each listed query.</summary>
    public static IEnumerable<Finding> StatisticsFindings(IReadOnlyList<Ranked> ranked, FindHotQueriesRequest request)
    {
        foreach (var (stats, query, tables) in ranked)
        {
            if (stats.Calls < request.MinCalls) continue;
            var table = $"{tables[0].Schema}.{tables[0].Name}";

            if (query.PagesPerRow is { } perRow && perRow > request.ReadsPerRowThreshold)
                yield return Fact(HotQueryRuleIds.ReadsPerRow, Severity.Medium, table, query,
                    $"Query {query.QueryId} reads {Num.N0(perRow)} pages for every row it returns",
                    $"Over {Num.N0(stats.Calls)} calls it read {Num.N0(stats.PagesRead)} pages of 8 KB and returned " +
                    $"{Num.N0(stats.Rows)} rows. A query that filters or joins through an index reads a few pages per " +
                    "row; this one reads through far more data than it returns. Its plan, when listed, says where. An " +
                    "aggregate (COUNT, SUM) returns one row from many by design: look first at queries that return rows.");

            if (stats.PagesRead >= 1_000 && query.DiskReadShare >= request.DiskReadShareThreshold)
                yield return Fact(HotQueryRuleIds.DiskReads, Severity.Low, table, query,
                    $"Query {query.QueryId} reads {Num.P0(query.DiskReadShare)} of its pages from outside the cache",
                    $"{Num.N0(stats.PagesFromDisk)} of the {Num.N0(stats.PagesRead)} pages it read were not in the database's " +
                    "cache. The data it touches does not stay in memory between calls: a narrower index, or reading fewer " +
                    "rows, keeps it there. On PostgreSQL these pages may still come from the operating system's cache.");

            if (stats.TempPages is > 0)
                yield return Fact(HotQueryRuleIds.TempSpill, Severity.Medium, table, query,
                    $"Query {query.QueryId} wrote {Num.N0(stats.TempPages.Value)} pages to temporary storage",
                    "A sort, hash or aggregate did not fit in its memory grant and spilled to disk " +
                    "(temporary files on PostgreSQL, tempdb on SQL Server), which is slower by orders of magnitude. " +
                    "An index that returns rows already ordered, or fewer rows to sort, avoids it.");
        }
    }

    private static Finding Fact(string ruleId, Severity severity, string table, HotQuery query, string title, string description) =>
        new(ruleId, severity, FindingCategory.Performance, 1.0, Evidence.Measured, table, null, title, description, null,
            new Dictionary<string, object> { ["queryId"] = query.QueryId });

    public static FindHotQueriesResult Build(
        IReadOnlyList<Ranked> ranked,
        IEnumerable<Finding> findings,
        HotQuerySourceInfo source,
        int plansRead,
        IReadOnlyList<string> caveats,
        VerificationSummary? verification = null)
    {
        var split = FindingSplit.Partition(findings).WithMandatory(Summary(ranked, source, plansRead));
        List<string> allCaveats = [.. caveats];
        if (split.TruncationCaveat() is { } truncation) allCaveats.Add(truncation);
        if (split.SignalCaveat() is { } signal) allCaveats.Add(signal);

        return new FindHotQueriesResult(
            [.. ranked.Select(r => r.Query)],
            split.Findings,
            split.Signals,
            new HotQueriesSummary(
                split.Findings.Count, split.Signals.Count, ranked.Count, plansRead,
                split.BySeverity(), split.ByRule(AllRuleIds)),
            source,
            allCaveats,
            split.Truncated,
            verification);
    }

    private static Finding Summary(IReadOnlyList<Ranked> ranked, HotQuerySourceInfo source, int plansRead) => new(
        HotQueryRuleIds.Summary, Severity.Info, FindingCategory.Performance, 1.0, Evidence.Derived,
        source.Source ?? "(none)", null,
        source.Source is null
            ? "No query statistics could be read"
            : $"{Num.N0(ranked.Count)} heaviest queries from {source.Source}",
        source.Source is null
            ? "Neither source of query statistics is available on this database: see Availability for what each needs. " +
              "Nothing was measured, which is not the same as no slow query."
            : $"Listed by total time, from {source.Source}" +
              (source.WindowDays is { } w ? $", which covers the last {Num.F1(w)} day(s)" : "") +
              $". Plans read: {Num.N0(plansRead)}. Query text is returned with every literal and comment removed.",
        null,
        null);

    // ── Tables named in a query ────────────────────────────────────────────────

    /// <summary>
    /// The user tables a query's text names. A schema-qualified name matches exactly; a bare name
    /// matches the one table of that name, and nothing when several schemas have one.
    /// </summary>
    public static IReadOnlyList<(string Schema, string Name)> TablesNamed(
        string sql, IReadOnlyList<(string Schema, string Name)> userTables, DatabaseDialect dialect)
    {
        var identifiers = Identifiers(sql, dialect);
        List<(string, string)> found = [];

        for (var i = 0; i < identifiers.Count; i++)
        {
            var name = identifiers[i];
            var candidates = userTables.Where(t => Same(t.Name, name)).ToList();
            if (candidates.Count == 0) continue;

            var qualified = i >= 2 && identifiers[i - 1] == "." ? identifiers[i - 2] : null;
            var match = qualified is not null
                ? candidates.FirstOrDefault(t => Same(t.Schema, qualified))
                : candidates.Count == 1 ? candidates[0] : default;

            if (match != default && !found.Contains(match)) found.Add(match);
        }

        return found;
    }

    /// <summary>Names in order, quoted ones unquoted, with "." kept so a qualified name can be read.</summary>
    private static List<string> Identifiers(string sql, DatabaseDialect dialect)
    {
        List<string> names = [];
        foreach (var segment in SqlText.Segments(sql, dialect))
        {
            if (segment.Kind == SqlSegmentKind.QuotedIdentifier)
            {
                names.Add(segment.Text);
                continue;
            }

            if (segment.Kind != SqlSegmentKind.Code) continue;

            var text = segment.Text;
            for (var i = 0; i < text.Length;)
            {
                if (char.IsLetter(text[i]) || text[i] == '_')
                {
                    var start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$')) i++;
                    names.Add(text[start..i]);
                }
                else
                {
                    if (text[i] == '.') names.Add(".");
                    else if (!char.IsWhiteSpace(text[i])) names.Add(text[i].ToString());
                    i++;
                }
            }
        }

        return names;
    }

    private static string CollapseSpaces(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
