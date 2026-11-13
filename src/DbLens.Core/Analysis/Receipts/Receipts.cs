using System.Globalization;

namespace DbLens.Core.Analysis.Receipts;

public sealed record QueryWithReceiptRequest(
    string Sql,
    /// <summary>The same figure computed another way: a second join path, or detail against total.</summary>
    string? CrossCheckSql = null,
    /// <summary>Relative difference allowed between numeric cells. 0 = exact.</summary>
    double Tolerance = 0.0,
    int MaxRows = 100);

public sealed record QueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated,
    double ElapsedMs);

public enum CrossCheckStatus { Agree, Disagree, NotComparable }

public sealed record CrossCheck(string Sql, CrossCheckStatus Status, string Explanation, QueryResult Result);

/// <summary>What the answer stands on: the exact SQL, what it read, and whether a second route agrees.</summary>
public sealed record Receipt(
    string Sql,
    IReadOnlyList<string> TablesTouched,
    string Database,
    string ExecutedAt,
    CrossCheck? CrossCheck,
    /// <summary>Set by the caller when the SQL is a query the user certified.</summary>
    string? CertifiedAs = null);

public sealed record QueryWithReceiptResult(QueryResult Result, Receipt Receipt, IReadOnlyList<string> Caveats);

/// <summary>Compares two result sets as multisets of rows. Pure.</summary>
public static class ResultComparer
{
    public static (CrossCheckStatus Status, string Explanation) Compare(QueryResult primary, QueryResult check, double tolerance)
    {
        if (primary.Truncated || check.Truncated)
            return (CrossCheckStatus.NotComparable, "At least one result was truncated, so the two cannot be compared in full.");

        if (primary.Columns.Count != check.Columns.Count)
            return (CrossCheckStatus.NotComparable,
                $"The queries return {primary.Columns.Count} and {check.Columns.Count} columns; they do not compute the same thing.");

        if (primary.Rows.Count != check.Rows.Count)
            return (CrossCheckStatus.Disagree,
                $"The queries return {primary.Rows.Count} and {check.Rows.Count} rows.");

        var a = Sorted(primary.Rows);
        var b = Sorted(check.Rows);

        for (var r = 0; r < a.Count; r++)
            for (var c = 0; c < a[r].Count; c++)
                if (!CellsEqual(a[r][c], b[r][c], tolerance))
                    return (CrossCheckStatus.Disagree,
                        $"Column {c + 1} ('{primary.Columns[c]}') differs: {Show(a[r][c])} versus {Show(b[r][c])}.");

        return (CrossCheckStatus.Agree,
            tolerance > 0
                ? $"Both queries return the same {a.Count} row(s), within a relative tolerance of {Num.P0(tolerance)}."
                : $"Both queries return exactly the same {a.Count} row(s).");
    }

    private static List<IReadOnlyList<object?>> Sorted(IReadOnlyList<IReadOnlyList<object?>> rows) =>
        rows.OrderBy(r => string.Join("\u001f", r.Select(Key)), StringComparer.Ordinal).ToList();

    private static string Key(object? v) => AsDecimal(v) is { } d ? d.ToString(CultureInfo.InvariantCulture) : Show(v);

    private static bool CellsEqual(object? x, object? y, double tolerance)
    {
        if (x is null || y is null) return x is null && y is null;

        if (AsDecimal(x) is { } dx && AsDecimal(y) is { } dy)
        {
            if (dx == dy) return true;
            var scale = Math.Max(Math.Abs((double)dx), Math.Abs((double)dy));
            return tolerance > 0 && Math.Abs((double)(dx - dy)) <= tolerance * scale;
        }

        return Show(x) == Show(y);
    }

    private static decimal? AsDecimal(object? v) => v switch
    {
        byte or short or int or long or decimal => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
        float f when float.IsFinite(f) => (decimal)f,
        double d when double.IsFinite(d) && Math.Abs(d) < 7.9e28 => (decimal)d,
        _ => null,
    };

    private static string Show(object? v) => v switch
    {
        null => "NULL",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };
}

/// <summary>Turns driver values into JSON-safe ones without changing what they say.</summary>
public static class ResultValue
{
    private const int MaxText = 500;

    public static object? From(object? value) => value switch
    {
        null or DBNull => null,
        bool or byte or short or int or long or decimal or float or double => value,
        string s => s.Length <= MaxText ? s : s[..MaxText] + "…",
        DateTime d => d.ToString("o", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("o", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("o", CultureInfo.InvariantCulture),
        TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
        byte[] b => $"<{b.Length} bytes>",
        Guid g => g.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
