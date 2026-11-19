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

public enum SqlTokenKind { Word, Parameter, Symbol, Space, QuotedIdentifier, StringLiteral, Comment }

/// <summary>
/// A token of SQL text: a word (a name or keyword), a positional parameter ($1), one symbol, a run
/// of spaces, or a whole quoted identifier, literal or comment. <see cref="Text"/> is as in
/// <see cref="SqlSegment"/>; <see cref="Source"/> rebuilds the text.
/// </summary>
public readonly record struct SqlToken(SqlTokenKind Kind, string Text, string Source);

/// <summary>
/// Lexical helpers for the rules that read SQL text. Not a SQL parser: they separate code from
/// comments, string literals and quoted identifiers the way each engine does, far enough for shape
/// matching and for the statement guard to see exactly the code the engine will see.
/// </summary>
public static partial class SqlText
{
    private static readonly Regex DollarTagRx =
        new(@"\G\$(?:[A-Za-z_\u0080-￿][A-Za-z0-9_\u0080-￿]*)?\$", RegexOptions.Compiled);

    /// <summary>
    /// Splits SQL in one pass by the engine's own lexical rules: nested block comments on both;
    /// dollar-quoted and E'' strings on PostgreSQL, where '[' is a subscript; [bracketed] identifiers
    /// on SQL Server. An unterminated comment or literal runs to the end, as it would fail on the engine.
    /// </summary>
    public static IReadOnlyList<SqlSegment> Segments(string sql, DatabaseDialect dialect)
    {
        var postgres = dialect == DatabaseDialect.PostgreSql;
        List<SqlSegment> segments = [];
        var code = new StringBuilder();
        var i = 0;

        void FlushCode()
        {
            if (code.Length == 0) return;
            segments.Add(new SqlSegment(SqlSegmentKind.Code, code.ToString(), code.ToString()));
            code.Clear();
        }

        void Emit(SqlSegmentKind kind, int end, string? text = null)
        {
            FlushCode();
            var source = sql[i..end];
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
                Emit(SqlSegmentKind.Comment, end < 0 ? sql.Length : end);
            }
            else if (c == '/' && next == '*')
            {
                Emit(SqlSegmentKind.Comment, EndOfBlockComment(sql, i));
            }
            else if (c == '\'')
            {
                // E'..' is the only PostgreSQL string where a backslash escapes the quote; the
                // transactions that run caller SQL pin standard_conforming_strings to match.
                var escapes = postgres && i > 0 && sql[i - 1] is 'e' or 'E'
                              && (i < 2 || !IsIdentifierChar(sql[i - 2]));
                Emit(SqlSegmentKind.StringLiteral, EndOfQuoted(sql, i, '\'', escapes));
            }
            else if (c == '"' || (c == '[' && !postgres))
            {
                var close = c == '"' ? '"' : ']';
                var end = EndOfQuoted(sql, i, close, backslashEscapes: false);
                Emit(SqlSegmentKind.QuotedIdentifier, end, Unquote(sql, i, end, close));
            }
            else if (c == '$' && postgres && (i == 0 || !IsIdentifierChar(sql[i - 1]))
                     && DollarTagRx.Match(sql, i) is { Success: true } tag)
            {
                var close = sql.IndexOf(tag.Value, i + tag.Length, StringComparison.Ordinal);
                Emit(SqlSegmentKind.StringLiteral, close < 0 ? sql.Length : close + tag.Length);
            }
            else
            {
                code.Append(c);
                i++;
            }
        }

        FlushCode();
        return segments;
    }

    /// <summary>
    /// The text with every comment replaced by a space and every string literal and quoted
    /// identifier replaced by what <paramref name="replace"/> returns for it.
    /// </summary>
    public static string Rewrite(IEnumerable<SqlSegment> segments, Func<SqlSegment, string> replace) =>
        string.Concat(segments.Select(s => s.Kind switch
        {
            SqlSegmentKind.Code => s.Text,
            SqlSegmentKind.Comment => " ",
            _ => replace(s),
        }));

    /// <summary>The text as tokens, over <see cref="Segments"/>: names and keywords are never read inside literals or comments.</summary>
    public static IEnumerable<SqlToken> Tokens(string sql, DatabaseDialect dialect)
    {
        foreach (var segment in Segments(sql, dialect))
        {
            if (segment.Kind != SqlSegmentKind.Code)
            {
                yield return new SqlToken(segment.Kind switch
                {
                    SqlSegmentKind.QuotedIdentifier => SqlTokenKind.QuotedIdentifier,
                    SqlSegmentKind.StringLiteral => SqlTokenKind.StringLiteral,
                    _ => SqlTokenKind.Comment,
                }, segment.Text, segment.Source);
                continue;
            }

            var text = segment.Text;
            for (var i = 0; i < text.Length;)
            {
                var start = i;
                SqlTokenKind kind;
                if (char.IsWhiteSpace(text[i]))
                {
                    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                    kind = SqlTokenKind.Space;
                }
                else if (char.IsLetter(text[i]) || text[i] == '_')
                {
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$')) i++;
                    kind = SqlTokenKind.Word;
                }
                else if (text[i] == '$' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
                {
                    i++;
                    while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                    kind = SqlTokenKind.Parameter;
                }
                else
                {
                    i++;
                    kind = SqlTokenKind.Symbol;
                }

                yield return new SqlToken(kind, text[start..i], text[start..i]);
            }
        }
    }

    /// <summary>
    /// What a query does, without what it was called with: every string and number literal becomes
    /// "?", every comment goes. Workload text can hold personal data in a WHERE clause or a comment,
    /// so this is the only form in which dblens returns it. Parameters ($1, @p0) stay as they are.
    /// </summary>
    public static string Redact(string sql, DatabaseDialect dialect) =>
        string.Concat(Segments(sql, dialect).Select(s => s.Kind switch
        {
            SqlSegmentKind.Code => (dialect == DatabaseDialect.SqlServer ? SqlServerNumberLiteral() : PostgresNumberLiteral()).Replace(s.Text, "?"),
            SqlSegmentKind.Comment => " ",
            SqlSegmentKind.StringLiteral => "?",
            _ => s.Source,
        }));

    // A number standing alone: not part of a name (t1, x.2), a parameter (@p1, PostgreSQL's $1) or a
    // temp table (#t1). PostgreSQL 16 also writes 1_000, 0o17 and 0b101; T-SQL writes money as $12.50.
    private const string Number =
        @"(?:0[xX][0-9A-Fa-f_]+|0[oO][0-7_]+|0[bB][01_]+|\d(?:_?\d)*(?:\.(?:\d(?:_?\d)*)?)?(?:[eE][+-]?\d+)?|\.\d(?:_?\d)*(?:[eE][+-]?\d+)?)(?![\w$])";

    [GeneratedRegex(@"(?<![\w$@#.])" + Number)]
    private static partial System.Text.RegularExpressions.Regex PostgresNumberLiteral();

    [GeneratedRegex(@"(?<![\w@#.])" + Number)]
    private static partial System.Text.RegularExpressions.Regex SqlServerNumberLiteral();

    /// <summary>
    /// The code alone: the contents of comments, literals and quoted identifiers can never be
    /// mistaken for keywords or statement separators.
    /// </summary>
    public static string StripCommentsAndStrings(string sql, DatabaseDialect dialect) =>
        Rewrite(Segments(sql, dialect), _ => " ");

    public static string StripComments(string sql, DatabaseDialect dialect) =>
        Rewrite(Segments(sql, dialect), s => s.Source);

    /// <summary>Splits a script into individual statements on semicolons, without its comments.</summary>
    public static IReadOnlyList<string> SplitStatements(string sql, DatabaseDialect dialect) =>
        SplitTopLevel(sql, ';', dialect);

    /// <summary>
    /// Splits on a separator that stands in code and outside any parentheses. Comments are dropped;
    /// literals and quoted identifiers are kept whole.
    /// </summary>
    public static IReadOnlyList<string> SplitTopLevel(string sql, char separator, DatabaseDialect dialect)
    {
        List<string> parts = [];
        var current = new StringBuilder();
        var depth = 0;

        void Flush()
        {
            var part = current.ToString().Trim();
            if (part.Length > 0) parts.Add(part);
            current.Clear();
        }

        foreach (var segment in Segments(sql, dialect))
        {
            if (segment.Kind != SqlSegmentKind.Code)
            {
                current.Append(segment.Kind == SqlSegmentKind.Comment ? " " : segment.Source);
                continue;
            }

            foreach (var c in segment.Text)
            {
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;

                if (c == separator && depth == 0) Flush();
                else current.Append(c);
            }
        }

        Flush();
        return parts;
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
