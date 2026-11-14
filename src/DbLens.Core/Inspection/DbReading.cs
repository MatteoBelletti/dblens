using System.Data.Common;
using System.Globalization;

namespace DbLens.Core.Inspection;

/// <summary>Reads catalog columns by name, with the null handling and widths both drivers need.</summary>
public static class DbReading
{
    public static string Text(this DbDataReader reader, string column) =>
        reader.GetString(reader.GetOrdinal(column));

    public static string? TextOrNull(this DbDataReader reader, string column) =>
        reader.ValueOrNull(column) as string;

    public static bool Flag(this DbDataReader reader, string column) =>
        reader.GetBoolean(reader.GetOrdinal(column));

    /// <summary>An integer of whatever width the engine chose: SQL Server's COUNT is int, PostgreSQL's bigint.</summary>
    public static long Number(this DbDataReader reader, string column) =>
        Convert.ToInt64(reader.GetValue(reader.GetOrdinal(column)), CultureInfo.InvariantCulture);

    public static long? NumberOrNull(this DbDataReader reader, string column) =>
        reader.ValueOrNull(column) is { } value ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : null;

    public static double? RealOrNull(this DbDataReader reader, string column) =>
        reader.ValueOrNull(column) is { } value ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : null;

    public static T Field<T>(this DbDataReader reader, string column) =>
        reader.GetFieldValue<T>(reader.GetOrdinal(column));

    private static object? ValueOrNull(this DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);
    }
}
