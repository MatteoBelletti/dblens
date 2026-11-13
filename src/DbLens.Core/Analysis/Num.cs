using System.Globalization;

namespace DbLens.Core.Analysis;

/// <summary>
/// Invariant number formatting for finding text. <see cref="Finding"/> is a product contract
/// consumed by machines, so its text must not change with the server's locale — a row count
/// must read "5,000,000" whether the process runs in en-US or it-IT.
/// </summary>
public static class Num
{
    /// <summary>Thousands-separated integer, e.g. "5,000,000".</summary>
    public static string N0(double value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Thousands-separated integer, e.g. "5,000,000".</summary>
    public static string N0(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>One decimal place, e.g. "42.7".</summary>
    public static string F1(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>No decimal places, e.g. "30".</summary>
    public static string F0(double value) => value.ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>Percentage from a 0..1 ratio, e.g. "70%".</summary>
    public static string P0(double ratio) => ratio.ToString("P0", CultureInfo.InvariantCulture);
}
