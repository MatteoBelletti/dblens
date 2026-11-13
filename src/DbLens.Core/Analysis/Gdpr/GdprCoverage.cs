using System.Text.RegularExpressions;
using DbLens.Core.Models;

namespace DbLens.Core.Analysis.Gdpr;

/// <summary>
/// What the scan could and could not see. A name-pattern scan that finds nothing has two very
/// different meanings — "there is no PII here" and "these names told me nothing" — and only this
/// record distinguishes them. Without it, an empty result reads as a clean bill of health.
/// </summary>
public sealed record GdprCoverage(
    int ColumnsScanned,
    /// <summary>Columns that matched a PII pattern.</summary>
    int ColumnsMatched,
    /// <summary>Columns that matched nothing.</summary>
    int ColumnsUnmatched,
    /// <summary>
    /// Unmatched columns whose names carry no usable signal (c1, col_3, fld2, nm). The scan
    /// cannot make any statement about these, in either direction.
    /// </summary>
    int ColumnsWithOpaqueNames,
    /// <summary>A sample of the opaque column names, for the caller to inspect directly.</summary>
    IReadOnlyList<string> OpaqueColumnExamples,
    /// <summary>Fraction of scanned columns the scan could actually reason about, 0..1.</summary>
    double InterpretableRatio);

public static class GdprCoverageAnalyzer
{
    private const int MaxExamples = 15;

    /// <summary>Names like c1, col_3, fld2, x7 — a positional label rather than a description.</summary>
    private static readonly Regex PositionalNameRx =
        new(@"^[a-z]{1,4}_?\d{1,3}$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Vowel-free abbreviations like nm, dsc, addr_ln — unreadable to a dictionary.</summary>
    private static readonly Regex NoVowelRx =
        new(@"^[^aeiou\d_]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static GdprCoverage Analyze(
        IReadOnlyList<TableDetail> tables,
        IReadOnlyCollection<(string Table, string Column)> matchedColumns)
    {
        var matched = new HashSet<(string, string)>(matchedColumns);

        var scanned = 0;
        var matchedCount = 0;
        var opaque = new List<string>();

        foreach (var table in tables)
        {
            var qualified = table.Qualified();

            foreach (var column in table.Columns)
            {
                scanned++;

                if (matched.Contains((qualified, column.Name)))
                {
                    matchedCount++;
                    continue;
                }

                if (IsOpaque(column.Name))
                    opaque.Add($"{qualified}.{column.Name}");
            }
        }

        var interpretable = scanned == 0 ? 1.0 : (double)(scanned - opaque.Count) / scanned;

        return new GdprCoverage(
            ColumnsScanned: scanned,
            ColumnsMatched: matchedCount,
            ColumnsUnmatched: scanned - matchedCount,
            ColumnsWithOpaqueNames: opaque.Count,
            OpaqueColumnExamples: opaque.Take(MaxExamples).ToList(),
            InterpretableRatio: Math.Round(interpretable, 4));
    }

    /// <summary>
    /// True when a column name carries no signal a dictionary could match. Deliberately narrow:
    /// a name that is merely absent from the dictionary (a real word in another language) is not
    /// reported here, because the scan cannot tell that case apart from a genuine non-match.
    /// </summary>
    public static bool IsOpaque(string columnName)
    {
        var name = columnName.Trim();

        if (name.Length <= 2) return true;
        if (PositionalNameRx.IsMatch(name)) return true;

        var letters = name.Replace("_", "");
        return letters.Length > 0 && NoVowelRx.IsMatch(letters);
    }

    /// <summary>The caveat to emit whenever the scan had blind spots. Null when coverage is complete.</summary>
    public static string? BuildCaveat(GdprCoverage coverage)
    {
        if (coverage.ColumnsWithOpaqueNames == 0) return null;

        return
            $"{coverage.ColumnsWithOpaqueNames} of {coverage.ColumnsScanned} scanned column(s) have names " +
            "that carry no usable signal (for example 'c1', 'col_3', 'nm'). " +
            "This scan matches column NAMES only and can make no statement about those columns in " +
            "either direction — they are not covered by this result. " +
            $"Examples: {string.Join(", ", coverage.OpaqueColumnExamples)}. " +
            "An empty or short findings list therefore does NOT mean the database is free of personal data. " +
            "Inspect those columns directly.";
    }

    /// <summary>
    /// The caveat that must accompany a scan matching nothing at all: the most misreadable result
    /// this tool can return.
    /// </summary>
    public static string BuildNoMatchCaveat(GdprCoverage coverage) =>
        $"No PII patterns matched across {coverage.ColumnsScanned} column(s). " +
        "This means the column names did not match the dictionaries — it is NOT a finding that the " +
        "database holds no personal data. Names in a language the dictionaries do not cover, " +
        "internal codenames, and generic names all scan clean while holding personal data. " +
        "Do not report this result as confirmation of compliance.";
}
