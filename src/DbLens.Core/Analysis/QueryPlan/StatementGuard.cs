
using System.Text.RegularExpressions;

namespace DbLens.Core.Analysis.QueryPlan;

/// <summary>
/// First of the two guards protecting EXPLAIN ANALYZE, which executes the statement it is given.
/// Rejects anything that is not a single, purely read-only statement.
///
/// This is a conservative allow-list, not a SQL parser: anything it cannot prove read-only is
/// rejected. The second guard (a read-only transaction opened by the inspector) is what actually
/// enforces the restriction at the engine level — this one exists so that hostile input never
/// reaches the database in the first place.
/// </summary>
public static class StatementGuard
{
    /// <summary>Keywords that write data or change schema, in any position of the statement.</summary>
    private static readonly string[] ForbiddenKeywords =
    [
        "insert", "update", "delete", "merge", "truncate", "drop", "create", "alter",
        "grant", "revoke", "call", "do", "copy", "vacuum", "analyze", "reindex", "cluster",
        "refresh", "lock", "comment", "security", "execute", "exec", "commit", "rollback",
        "savepoint", "begin", "set", "reset", "prepare", "deallocate", "listen", "notify",
        "checkpoint", "backup", "restore", "shutdown", "kill", "dbcc", "waitfor", "openrowset",
        "opendatasource", "xp_cmdshell", "sp_executesql", "bulk", "into",
    ];

    private static readonly Regex WordRx =
        new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    public sealed record GuardResult(bool Allowed, string? RejectionReason)
    {
        public static GuardResult Allow() => new(true, null);
        public static GuardResult Reject(string reason) => new(false, reason);
    }

    /// <summary>
    /// Returns whether the statement may be executed by EXPLAIN ANALYZE.
    /// Only a single SELECT (optionally preceded by a read-only WITH clause) is permitted.
    /// </summary>
    public static GuardResult IsReadOnlySelect(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return GuardResult.Reject("The statement is empty.");

        var stripped = StripCommentsAndStrings(sql);

        if (stripped.Trim().Length == 0)
            return GuardResult.Reject("The statement contains no executable SQL once comments are removed.");

        if (ContainsStatementSeparator(stripped))
            return GuardResult.Reject(
                "The input contains more than one statement. EXPLAIN ANALYZE accepts exactly one " +
                "SELECT statement; batching is rejected so that a read-only statement cannot carry " +
                "a second, writing statement alongside it.");

        var words = WordRx.Matches(stripped)
            .Select(m => m.Value.ToLowerInvariant())
            .ToList();

        if (words.Count == 0)
            return GuardResult.Reject("The statement contains no recognizable SQL keywords.");

        var first = words[0];
        if (first is not ("select" or "with"))
            return GuardResult.Reject(
                $"Only SELECT statements can be executed. This statement starts with '{first.ToUpperInvariant()}'. " +
                "Run it without analyze to obtain an estimated plan without executing anything.");

        // PostgreSQL allows data-modifying CTEs: WITH x AS (DELETE FROM t RETURNING *) SELECT * FROM x
        // starts with WITH but writes. Forbidden keywords are therefore checked across the whole
        // statement, not just its first word.
        var offending = words.FirstOrDefault(w => ForbiddenKeywords.Contains(w));
        if (offending is not null)
            return GuardResult.Reject(
                $"The statement contains the keyword '{offending.ToUpperInvariant()}', which can write data " +
                "or change schema. Only purely read-only SELECT statements can be executed. " +
                "Run it without analyze to obtain an estimated plan without executing anything.");

        return GuardResult.Allow();
    }

    /// <summary>
    /// Removes comments and string/quoted-identifier literals. Literals are blanked so that their
    /// contents can never be mistaken for keywords or statement separators.
    /// </summary>
    public static string StripCommentsAndStrings(string sql) => SqlText.StripCommentsAndStrings(sql);

    /// <summary>
    /// True when the text holds more than one statement. A single trailing semicolon is allowed.
    /// </summary>
    private static bool ContainsStatementSeparator(string strippedSql)
    {
        var trimmed = strippedSql.TrimEnd();

        var semicolon = trimmed.IndexOf(';');
        if (semicolon < 0) return false;

        // Anything other than whitespace after the first semicolon means a second statement.
        return trimmed[(semicolon + 1)..].Trim().Length > 0;
    }
}
