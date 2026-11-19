using DbLens.Core.Analysis.Verification;
using DbLens.Core.Models;
using static DbLens.Core.Analysis.Names;

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
        string defaultSchema,
        FindHotQueriesRequest request) =>
        // Sorted first, then read lazily: only as many texts are redacted and read as the list needs.
        [.. stats
            .OrderByDescending(s => s.TotalTimeMs)
            .Where(s => !ProbeColumns.IsProbe(s.Text))
            .Select(s => (Stats: s, Redacted: SqlText.Redact(s.Text, dialect)))
            .Where(x => IsDataStatement(x.Redacted))
            .Select(x => (x.Stats, x.Redacted, Tables: TablesNamed(x.Stats.Text, userTables, dialect, defaultSchema)))
            .Where(x => x.Tables.Count > 0)
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
    public static IEnumerable<Finding> StatisticsFindings(IReadOnlyList<Ranked> ranked, FindHotQueriesRequest request, DatabaseDialect dialect)
    {
        foreach (var (stats, query, tables) in ranked)
        {
            if (stats.Calls < request.MinCalls) continue;
            var table = $"{tables[0].Schema}.{tables[0].Name}";

            if (query.PagesPerRow is { } perRow && perRow > request.ReadsPerRowThreshold)
                yield return Fact(HotQueryRuleIds.ReadsPerRow, Severity.Medium, table, query,
                    $"Query {query.QueryId} reads {Num.N0(perRow)} pages for every row it returns",
                    $"Over {Num.N0(stats.Calls)} calls it read {Num.N0(stats.PagesRead)} pages of 8 KB and returned " +
                    $"{Num.N0(stats.Rows)} rows. One that goes through an index reads a few pages per row: this one reads " +
                    "through far more data than it returns. Its plan, when listed, " +
                    "says where. An aggregate (COUNT, SUM) returns one row from many by design: look first at queries that return rows.");

            if (stats.PagesRead >= request.DiskReadMinPages && query.DiskReadShare >= request.DiskReadShareThreshold)
                yield return Fact(HotQueryRuleIds.DiskReads, Severity.Low, table, query,
                    $"Query {query.QueryId} reads {Num.P0(query.DiskReadShare)} of its pages from outside the cache",
                    $"{Num.N0(stats.PagesFromDisk)} of the {Num.N0(stats.PagesRead)} pages it read were not in the database's " +
                    "cache. The data it touches does not stay in memory between calls: a narrower index, or reading fewer " +
                    "rows, keeps it there. On PostgreSQL these pages may still come from the operating system's cache.");

            if (stats.TempPages is > 0)
                yield return Fact(HotQueryRuleIds.TempSpill, Severity.Medium, table, query,
                    $"Query {query.QueryId} wrote {Num.N0(stats.TempPages.Value)} pages to temporary storage",
                    (dialect == DatabaseDialect.SqlServer
                        ? "On SQL Server this is tempdb space: temporary tables and table variables count too. A sort, " +
                          "hash or aggregate that does not fit in its memory grant also spills there, "
                        : "A sort, hash or aggregate did not fit in work_mem and spilled to temporary files, ") +
                    "which is slower by orders of magnitude. An index that returns rows already ordered, or fewer " +
                    "rows to sort, avoids a spill.");
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
            ? "No source of query statistics could be read on this database: see Availability for why, and what would " +
              "enable one. " +
              "Nothing was measured, which is not the same as no slow query."
            : $"Listed by total time, from {source.Source}" +
              (source.WindowDays is { } w ? $", which covers the last {Num.F1(w)} day(s)" : "") +
              $". Plans read: {Num.N0(plansRead)}. Query text is returned with every literal replaced by ? and every comment removed.",
        null,
        null);

    // ── Tables named in a query ────────────────────────────────────────────────

    /// <summary>
    /// The user tables a query reads or writes: names in a table position (after FROM, JOIN, UPDATE,
    /// INTO, USING, MERGE, or a comma of a FROM list), never a column or function that shares a table's name.
    /// A bare name resolves to the one table of that name, else to the one in the default schema.
    /// </summary>
    public static IReadOnlyList<(string Schema, string Name)> TablesNamed(
        string sql, IReadOnlyList<(string Schema, string Name)> userTables, DatabaseDialect dialect, string defaultSchema)
    {
        var tokens = Identifiers(sql, dialect);
        List<(string, string)> found = [];
        Stack<bool> outer = [];
        var inFromList = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token == "(") { outer.Push(inFromList); inFromList = false; continue; }
            if (token == ")") { inFromList = outer.Count > 0 && outer.Pop(); continue; }
            if (ListEnds.Contains(token)) { inFromList = false; continue; }

            var opens = TableKeywords.Contains(token);
            if (!opens && !(token == "," && inFromList)) continue;
            if (opens) inFromList = true;

            // schema.table, or db.schema.table on SQL Server: the last two parts.
            List<string> parts = [];
            var j = i + 1;
            if (j < tokens.Count && tokens[j] is var modifier && (Same(modifier, "ONLY") || Same(modifier, "LATERAL"))) j++;
            while (j < tokens.Count && tokens[j] is not ("." or "," or "(" or ")"))
            {
                parts.Add(tokens[j]);
                if (j + 1 < tokens.Count && tokens[j + 1] == ".") j += 2;
                else break;
            }

            if (parts.Count == 0) continue;
            var name = parts[^1];
            var schema = parts.Count > 1 ? parts[^2] : null;
            var candidates = userTables.Where(t => Same(t.Name, name)).ToList();
            var match = schema is not null
                ? Single(candidates.Where(t => Same(t.Schema, schema)), schema, name)
                : candidates.Count == 1 ? candidates[0] : Single(candidates.Where(t => Same(t.Schema, defaultSchema)), null, name);

            if (match != default && !found.Contains(match)) found.Add(match);
        }

        return found;
    }

    /// <summary>The one table that matches, by case when several differ only in it; none otherwise.</summary>
    private static (string Schema, string Name) Single(IEnumerable<(string Schema, string Name)> candidates, string? schema, string name)
    {
        var list = candidates.ToList();
        if (list.Count <= 1) return list.FirstOrDefault();
        var exact = list.Where(t => t.Name == name && (schema is null || t.Schema == schema)).ToList();
        return exact.Count == 1 ? exact[0] : default;
    }

    private static readonly HashSet<string> TableKeywords = new(["FROM", "JOIN", "UPDATE", "INTO", "USING", "MERGE"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ListEnds = new(
        ["WHERE", "ON", "GROUP", "ORDER", "HAVING", "SET", "SELECT", "VALUES", "LIMIT", "OFFSET", "UNION", "EXCEPT",
         "INTERSECT", "RETURNING", "OUTPUT", "WINDOW", "FETCH", "WHEN", ";"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Names in order, quoted ones unquoted, with "." kept so a qualified name can be read. PostgreSQL
    /// folds an unquoted name to lower case, so it is read that way.
    /// </summary>
    private static List<string> Identifiers(string sql, DatabaseDialect dialect) =>
    [
        .. SqlText.Tokens(sql, dialect).Select(t => t.Kind switch
        {
            SqlTokenKind.QuotedIdentifier or SqlTokenKind.Symbol or SqlTokenKind.Parameter => t.Text,
            SqlTokenKind.Word => dialect == DatabaseDialect.PostgreSql ? t.Text.ToLowerInvariant() : t.Text,
            _ => null,
        }).OfType<string>(),
    ];

    private static string CollapseSpaces(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
