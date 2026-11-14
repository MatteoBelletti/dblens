using System.Text.RegularExpressions;
using DbLens.Core.Models;

namespace DbLens.Core.Analysis.QueryPlan;

/// <summary>
/// Decides whether a caller's statement may be executed (EXPLAIN ANALYZE, query_with_receipt).
///
/// This is a filter, not a proof of read-only-ness: it rejects anything that is not a single
/// SELECT, every keyword that writes or changes state, functions with side effects (terminating
/// backends, file access, sequences, advisory locks, remote execution through OPENQUERY / dblink),
/// locking clauses and linked-server names. What it cannot see — a user-defined function that
/// writes, a privilege it does not know about — is stopped by the layers behind it: a transaction
/// that is always rolled back (READ ONLY on PostgreSQL) and the refusal to execute caller SQL on a
/// login that can write. The read-only login is the real defence; this guard keeps hostile input
/// from reaching the database at all.
///
/// The text is lexed by the target engine's rules (<see cref="SqlText.Segments"/>): a guard that
/// disagrees with the engine about where a string ends can be shown one statement while the engine
/// runs two.
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
        "opendatasource", "openquery", "xp_cmdshell", "sp_executesql", "bulk", "into",
        "updlock", "xlock", "tablock", "tablockx", "holdlock", "paglock",
    ];

    /// <summary>
    /// T-SQL needs no separator between statements, so a second statement can follow a SELECT
    /// without a semicolon. These start the ones the common list does not already catch; all are
    /// reserved words there, so none can be an unquoted column name.
    /// </summary>
    private static readonly string[] SqlServerForbiddenKeywords =
    [
        "deny", "reconfigure", "writetext", "updatetext", "readtext", "raiserror", "trigger",
    ];

    /// <summary>Functions with side effects, rejected wherever they are called.</summary>
    private static readonly HashSet<string> ForbiddenFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        // PostgreSQL: server control, files, sequences, notifications, executing SQL from a string.
        "pg_terminate_backend", "pg_cancel_backend", "pg_reload_conf", "pg_rotate_logfile", "pg_promote",
        "pg_switch_wal", "pg_create_restore_point", "pg_backup_start", "pg_backup_stop", "pg_start_backup",
        "pg_stop_backup", "pg_read_file", "pg_read_binary_file", "pg_stat_file",
        "pg_log_backend_memory_contexts", "pg_import_system_collations", "pg_notify", "pg_logical_emit_message",
        "set_config", "nextval", "setval", "txid_current", "pg_current_xact_id",
        "query_to_xml", "query_to_xmlschema", "query_to_xml_and_xmlschema", "cursor_to_xml",
        "pg_stat_statements_reset", "gin_clean_pending_list",
        // SQL Server: remote and ad hoc execution.
        "openquery", "openrowset", "opendatasource",
    };

    /// <summary>
    /// Function families rejected by prefix: large objects, server files, locks, replication,
    /// statistics resets, index maintenance, remote SQL.
    /// </summary>
    private static readonly string[] ForbiddenFunctionPrefixes =
    [
        "lo_", "pg_file_", "pg_ls_", "pg_advisory", "pg_try_advisory", "pg_sleep", "pg_create_", "pg_drop_",
        "pg_copy_", "pg_replication_", "pg_logical_", "pg_wal_replay", "pg_stat_reset", "brin_summarize",
        "brin_desummarize", "dblink", "xp_", "sp_",
    ];

    private static readonly Regex FunctionCallRx =
        new(@"\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);

    private static readonly Regex LockingClauseRx =
        new(@"\bFOR\s+(?:NO\s+KEY\s+UPDATE|KEY\s+SHARE|SHARE|UPDATE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // SQL Server's sequence syntax: the value is consumed even though the transaction rolls back.
    private static readonly Regex NextValueForRx =
        new(@"\bNEXT\s+VALUE\s+FOR\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // server.database.schema.object: a linked server, whose queries run under another server's rules.
    private static readonly Regex FourPartNameRx =
        new(@"\b\w+\s*\.\s*\w+\s*\.\s*\w+\s*\.\s*\w+\b", RegexOptions.Compiled);

    // U&"d\0061ta" spells an identifier in escapes, so its name cannot be read without decoding it.
    private static readonly Regex UnicodeIdentifierPrefixRx =
        new(@"(?<![A-Za-z0-9_$\u0080-￿])[uU]&$", RegexOptions.Compiled);

    private static readonly Regex WordRx =
        new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    public sealed record GuardResult(bool Allowed, string? RejectionReason)
    {
        public static GuardResult Allow() => new(true, null);
        public static GuardResult Reject(string reason) => new(false, reason);
    }

    /// <summary>
    /// Whether the text is exactly one statement. This is all an estimated plan needs — the engine
    /// plans the statement without running it — but a second statement after a semicolon would be
    /// sent, and run, alongside it.
    /// </summary>
    public static GuardResult IsSingleStatement(string? sql, DatabaseDialect dialect)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return GuardResult.Reject("The statement is empty.");

        var code = SqlText.StripCommentsAndStrings(sql, dialect);

        if (code.Trim().Length == 0)
            return GuardResult.Reject("The statement contains no executable SQL once comments are removed.");

        return ContainsStatementSeparator(code)
            ? GuardResult.Reject(
                "The input contains more than one statement. Exactly one statement is accepted; batching " +
                "is rejected so that a statement cannot carry a second one, which would be executed, alongside it.")
            : GuardResult.Allow();
    }

    /// <summary>
    /// Returns whether the statement may be executed on the given engine.
    /// Only a single SELECT (optionally preceded by a read-only WITH clause) is permitted.
    /// </summary>
    public static GuardResult IsReadOnlySelect(string? sql, DatabaseDialect dialect)
    {
        var single = IsSingleStatement(sql, dialect);
        if (!single.Allowed) return single;

        var segments = SqlText.Segments(sql!, dialect);
        var code = SqlText.Rewrite(segments, _ => " ");

        var words = WordRx.Matches(code)
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
        var offending = words.FirstOrDefault(w =>
            ForbiddenKeywords.Contains(w)
            || (dialect == DatabaseDialect.SqlServer && SqlServerForbiddenKeywords.Contains(w)));
        if (offending is not null)
            return GuardResult.Reject(
                $"The statement contains the keyword '{offending.ToUpperInvariant()}', which can write data " +
                "or change schema. Only purely read-only SELECT statements can be executed. " +
                $"If '{offending}' is a column or table name here, quote it. " +
                "Run it without analyze to obtain an estimated plan without executing anything.");

        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].Kind != SqlSegmentKind.QuotedIdentifier) continue;

            var name = segments[i].Text;
            if (IsForbiddenFunction(name))
                return GuardResult.Reject($"The statement references '{name}', which has side effects.");

            if (dialect == DatabaseDialect.PostgreSql && i > 0 && segments[i - 1].Kind == SqlSegmentKind.Code
                && UnicodeIdentifierPrefixRx.IsMatch(segments[i - 1].Text))
                return GuardResult.Reject(
                    "Unicode-escaped identifiers (U&\"…\") are not allowed: their names cannot be checked. " +
                    "Write the identifier plainly.");
        }

        // Function names and dotted chains survive in a copy where quoted identifiers become a
        // placeholder rather than a blank, so [srv].[db].[dbo].[t] is still read as four parts.
        var shape = SqlText.Rewrite(segments, s => s.Kind == SqlSegmentKind.StringLiteral ? "''" : "q");

        foreach (Match call in FunctionCallRx.Matches(shape))
        {
            var name = call.Groups["name"].Value;
            if (IsForbiddenFunction(name))
                return GuardResult.Reject(
                    $"The statement calls '{name}', which has side effects (server control, file access, sequences, " +
                    "locks or remote execution). Only side-effect-free SELECT statements can be executed.");
        }

        if (NextValueForRx.IsMatch(code))
            return GuardResult.Reject("NEXT VALUE FOR consumes a sequence value, which a rollback does not return.");

        if (LockingClauseRx.IsMatch(code))
            return GuardResult.Reject("Locking clauses (FOR UPDATE / FOR SHARE) take row locks and are not allowed.");

        if (FourPartNameRx.IsMatch(shape))
            return GuardResult.Reject(
                "Four-part names address a linked server, which runs the query under another server's rules. " +
                "Only objects of the connected database can be read.");

        return GuardResult.Allow();
    }

    private static bool IsForbiddenFunction(string name) =>
        ForbiddenFunctions.Contains(name)
        || ForbiddenFunctionPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the text holds more than one statement. A single trailing semicolon is allowed.
    /// </summary>
    private static bool ContainsStatementSeparator(string code)
    {
        var trimmed = code.TrimEnd();

        var semicolon = trimmed.IndexOf(';');
        if (semicolon < 0) return false;

        // Anything other than whitespace after the first semicolon means a second statement.
        return trimmed[(semicolon + 1)..].Trim().Length > 0;
    }
}
