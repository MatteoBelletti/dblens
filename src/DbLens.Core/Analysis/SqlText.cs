using System.Text;
using System.Text.RegularExpressions;
using DbLens.Core.Models;

namespace DbLens.Core.Analysis;

public enum SqlSegmentKind { Code, Comment, StringLiteral, QuotedIdentifier }

/// <summary>
/// A stretch of SQL text of one lexical kind. For a quoted identifier <see cref="Text"/> is the
/// name itself, unquoted and unescaped; for everything else it is the source text.
/// </summary>
public readonly record struct SqlSegment(SqlSegmentKind Kind, string Text, string Source);

/// <summary>
/// Shared lexical helpers for the rules that read SQL text.
/// These are deliberately not a SQL parser — they separate code from comments, string literals and
/// quoted identifiers the way each engine does, far enough for shape matching and for the statement
/// guard to see exactly the code the engine will see.
/// </summary>
public static class SqlText
{
    private static readonly Regex LineCommentRx =
        new(@"--[^\n\r]*", RegexOptions.Compiled);

    private static readonly Regex BlockCommentRx =
        new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex DollarTagRx =
        new(@"\G\$(?:[A-Za-z_\u0080-￿][A-Za-z0-9_\u0080-￿]*)?\$", RegexOptions.Compiled);

    /// <summary>
    /// Replaces comments with a space, leaving all identifiers and literals intact. Not literal-aware:
    /// for text that is only read, never for deciding what may execute.
    /// </summary>
    public static string StripComments(string sql)
    {
        var result = BlockCommentRx.Replace(sql, " ");
        return LineCommentRx.Replace(result, " ");
    }

    /// <summary>
    /// Splits SQL into code, comments, string literals and quoted identifiers in one pass, following
    /// the engine's own lexical rules: nested block comments on both; dollar-quoted and E'' strings
    /// on PostgreSQL, where '[' is a subscript; [bracketed] identifiers on SQL Server. An
    /// unterminated comment or literal runs to the end of the text, as it would fail on the engine.
    /// </summary>
    public static IReadOnlyList<SqlSegment> Segments(string sql, DatabaseDialect dialect)
    {
        var postgres = dialect == DatabaseDialect.PostgreSql;
        var segments = new List<SqlSegment>();
        var code = new StringBuilder();
        var i = 0;

        void Emit(SqlSegmentKind kind, int start, int end, string? text = null)
        {
            if (code.Length > 0)
            {
                segments.Add(new SqlSegment(SqlSegmentKind.Code, code.ToString(), code.ToString()));
                code.Clear();
            }

            var source = sql[start..end];
            segments.Add(new SqlSegment(kind, text ?? source, source));
            i = end;
        }

        while (i < sql.Length)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (c == '-' && next == '-')
            {
                var end = sql.IndexOfAny(['\n', '\r'], i);
                Emit(SqlSegmentKind.Comment, i, end < 0 ? sql.Length : end);
            }
            else if (c == '/' && next == '*')
            {
                Emit(SqlSegmentKind.Comment, i, EndOfBlockComment(sql, i));
            }
            else if (c == '\'')
            {
                // E'..' is the only PostgreSQL string where a backslash escapes the quote; the
                // transactions that run caller SQL pin standard_conforming_strings to match.
                var escapes = postgres && i > 0 && sql[i - 1] is 'e' or 'E'
                              && (i < 2 || !IsIdentifierChar(sql[i - 2]));
                Emit(SqlSegmentKind.StringLiteral, i, EndOfQuoted(sql, i, '\'', escapes));
            }
            else if (c == '"')
            {
                var end = EndOfQuoted(sql, i, '"', backslashEscapes: false);
                Emit(SqlSegmentKind.QuotedIdentifier, i, end, Unquote(sql, i, end, '"'));
            }
            else if (c == '[' && !postgres)
            {
                var end = EndOfQuoted(sql, i, ']', backslashEscapes: false);
                Emit(SqlSegmentKind.QuotedIdentifier, i, end, Unquote(sql, i, end, ']'));
            }
            else if (c == '$' && postgres && (i == 0 || !IsIdentifierChar(sql[i - 1]))
                     && DollarTagRx.Match(sql, i) is { Success: true } tag)
            {
                var close = sql.IndexOf(tag.Value, i + tag.Length, StringComparison.Ordinal);
                Emit(SqlSegmentKind.StringLiteral, i, close < 0 ? sql.Length : close + tag.Length);
            }
            else
            {
                code.Append(c);
                i++;
            }
        }

        if (code.Length > 0)
            segments.Add(new SqlSegment(SqlSegmentKind.Code, code.ToString(), code.ToString()));

        return segments;
    }

    /// <summary>
    /// The code alone: comments, string literals and quoted identifiers are each replaced by a
    /// space, so their contents can never be mistaken for keywords or statement separators.
    /// </summary>
    public static string StripCommentsAndStrings(string sql, DatabaseDialect dialect) =>
        string.Concat(Segments(sql, dialect).Select(s => s.Kind == SqlSegmentKind.Code ? s.Text : " "));

    /// <summary>Splits a script into individual statements on top-level semicolons, without its comments.</summary>
    public static IReadOnlyList<string> SplitStatements(string sql, DatabaseDialect dialect)
    {
        var statements = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            var statement = current.ToString().Trim();
            if (statement.Length > 0) statements.Add(statement);
            current.Clear();
        }

        foreach (var segment in Segments(sql, dialect))
        {
            if (segment.Kind == SqlSegmentKind.Comment)
            {
                current.Append(' ');
                continue;
            }

            if (segment.Kind != SqlSegmentKind.Code)
            {
                current.Append(segment.Source);
                continue;
            }

            foreach (var c in segment.Text)
            {
                if (c == ';') Flush();
                else current.Append(c);
            }
        }

        Flush();
        return statements;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' || c >= 0x80;

    private static int EndOfBlockComment(string sql, int start)
    {
        var depth = 0;
        var i = start;

        while (i < sql.Length - 1)
        {
            if (sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; }
            else if (sql[i] == '*' && sql[i + 1] == '/')
            {
                i += 2;
                if (--depth == 0) return i;
            }
            else i++;
        }

        return sql.Length;
    }

    /// <summary>The index just past the closing delimiter; a doubled delimiter is an escaped one.</summary>
    private static int EndOfQuoted(string sql, int start, char close, bool backslashEscapes)
    {
        for (var i = start + 1; i < sql.Length; i++)
        {
            if (backslashEscapes && sql[i] == '\\') { i++; continue; }
            if (sql[i] != close) continue;
            if (i + 1 < sql.Length && sql[i + 1] == close) { i++; continue; }
            return i + 1;
        }

        return sql.Length;
    }

    private static string Unquote(string sql, int start, int end, char close)
    {
        var terminated = end - start >= 2 && sql[end - 1] == close;
        var inner = sql[(start + 1)..(terminated ? end - 1 : end)];
        return inner.Replace(new string(close, 2), close.ToString(), StringComparison.Ordinal);
    }
}
