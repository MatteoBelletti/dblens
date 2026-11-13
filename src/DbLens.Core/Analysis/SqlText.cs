using System.Text;
using System.Text.RegularExpressions;

namespace DbLens.Core.Analysis;

/// <summary>
/// Shared lexical helpers for the rules that read SQL text.
/// These are deliberately not a SQL parser — they normalize text far enough for shape matching.
/// </summary>
public static class SqlText
{
    private static readonly Regex LineCommentRx =
        new(@"--[^\n\r]*", RegexOptions.Compiled);

    private static readonly Regex BlockCommentRx =
        new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Replaces comments with a space, leaving all identifiers and literals intact.</summary>
    public static string StripComments(string sql)
    {
        var result = BlockCommentRx.Replace(sql, " ");
        return LineCommentRx.Replace(result, " ");
    }

    /// <summary>
    /// Removes comments and blanks the contents of string literals and quoted identifiers, so
    /// their contents can never be mistaken for keywords or statement separators.
    /// </summary>
    public static string StripCommentsAndStrings(string sql)
    {
        var withoutComments = StripComments(sql);

        var sb = new StringBuilder(withoutComments.Length);
        char? quote = null;

        for (var i = 0; i < withoutComments.Length; i++)
        {
            var c = withoutComments[i];

            if (quote is null)
            {
                if (c is '\'' or '"' or '[')
                {
                    quote = c == '[' ? ']' : c;
                    sb.Append(' ');
                    continue;
                }
                sb.Append(c);
            }
            else
            {
                // Doubled quotes are an escaped quote inside the literal, not a terminator.
                if (c == quote)
                {
                    if (i + 1 < withoutComments.Length && withoutComments[i + 1] == c)
                    {
                        i++;
                        continue;
                    }
                    quote = null;
                }
                // Everything inside a literal is discarded.
            }
        }

        return sb.ToString();
    }

    /// <summary>Splits a script into individual statements on top-level semicolons.</summary>
    public static IReadOnlyList<string> SplitStatements(string sql)
    {
        var withoutComments = StripComments(sql);
        var statements = new List<string>();
        var current = new StringBuilder();
        char? quote = null;

        foreach (var c in withoutComments)
        {
            if (quote is null)
            {
                if (c is '\'' or '"' or '[')
                {
                    quote = c == '[' ? ']' : c;
                    current.Append(c);
                    continue;
                }

                if (c == ';')
                {
                    if (current.ToString().Trim().Length > 0)
                        statements.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }
            }
            else if (c == quote)
            {
                quote = null;
            }

            current.Append(c);
        }

        if (current.ToString().Trim().Length > 0)
            statements.Add(current.ToString().Trim());

        return statements;
    }
}
