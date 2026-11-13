using System.Text.RegularExpressions;

namespace DbLens.Core.Analysis.Denormalization;

/// <summary>
/// Shared name vocabulary for the denormalization heuristics.
/// These rules read column names only — never row data — so the vocabulary is
/// deliberately conservative: a name must be a strong signal before it is flagged.
/// </summary>
public static class ColumnNameVocabulary
{
    /// <summary>
    /// Names that legitimately repeat across almost every schema. They carry no
    /// denormalization signal and must never trigger the duplicated-column rule.
    /// </summary>
    public static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "uuid", "guid", "code", "name", "title", "label", "slug",
        "status", "state", "type", "kind", "category", "level", "position", "sort_order", "ordering",
        "description", "comment", "comments", "note", "notes", "text", "body", "content",
        "value", "data", "payload", "metadata", "config", "settings", "options",
        "created_at", "updated_at", "deleted_at", "modified_at", "inserted_at",
        "created_on", "updated_on", "created", "updated", "timestamp", "date", "time",
        "created_by", "updated_by", "deleted_by", "owner", "author",
        "version", "revision", "hash", "checksum", "etag",
        "active", "is_active", "enabled", "is_enabled", "is_deleted", "deleted",
        "visible", "is_visible", "locked", "is_locked", "is_default",
        "url", "uri", "path", "filename", "extension", "mime_type", "size",
        "locale", "language", "lang", "currency", "timezone", "country",
    };

    /// <summary>
    /// Which kind of aggregate a column name suggests. The distinction decides whether the
    /// heuristic can be verified against the data at all.
    /// </summary>
    public enum AggregateKind
    {
        /// <summary>Not an aggregate name.</summary>
        None = 0,

        /// <summary>
        /// A count of related rows. Verifiable: <c>COUNT(*)</c> over the child rows is
        /// unambiguous and needs no knowledge of which column was aggregated.
        /// </summary>
        CountLike = 1,

        /// <summary>
        /// A sum, average or extremum. NOT verifiable: the source column being aggregated is
        /// nowhere in the catalog. Checking <c>orders.total</c> against <c>COUNT(order_items)</c>
        /// would disagree on almost every row and publish a measured falsehood.
        /// </summary>
        SumLike = 2,
    }

    private static readonly string[] CountSuffixes = ["_count"];
    private static readonly string[] CountPrefixes = ["count_", "num_", "nb_"];

    private static readonly HashSet<string> CountExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "item_count", "row_count",
    };

    private static readonly string[] SumSuffixes =
        ["_total", "_sum", "_avg", "_average", "_min", "_max", "_qty", "_quantity", "_subtotal"];

    private static readonly string[] SumPrefixes =
        ["total_", "sum_", "avg_", "average_", "min_", "max_"];

    private static readonly HashSet<string> SumExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "total", "subtotal", "grand_total", "line_total",
    };

    /// <summary>Suffixes that indicate a cached aggregate of child rows.</summary>
    public static readonly string[] AggregateSuffixes = [..CountSuffixes, ..SumSuffixes];

    /// <summary>Prefixes that indicate a cached aggregate of child rows.</summary>
    public static readonly string[] AggregatePrefixes = [..CountPrefixes, ..SumPrefixes];

    /// <summary>Standalone names that are almost always cached aggregates.</summary>
    public static readonly HashSet<string> AggregateExactNames =
        new(CountExactNames.Concat(SumExactNames), StringComparer.OrdinalIgnoreCase);

    /// <summary>Classifies an aggregate-looking column name. Counts are checkable; sums are not.</summary>
    public static AggregateKind AggregateFamily(string columnName)
    {
        if (CountExactNames.Contains(columnName)) return AggregateKind.CountLike;
        if (SumExactNames.Contains(columnName)) return AggregateKind.SumLike;

        if (CountSuffixes.Any(s => columnName.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            || CountPrefixes.Any(p => columnName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return AggregateKind.CountLike;

        if (SumSuffixes.Any(s => columnName.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            || SumPrefixes.Any(p => columnName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return AggregateKind.SumLike;

        return AggregateKind.None;
    }

    /// <summary>Names that indicate a value concatenated from other columns in the same table.</summary>
    public static readonly HashSet<string> ConcatenatedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "full_name", "fullname", "display_name", "displayname", "complete_name",
        "full_address", "fulladdress", "formatted_address", "full_title",
    };

    /// <summary>Exact names that suggest several values packed into one scalar column.</summary>
    public static readonly HashSet<string> MultiValueExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "tags", "keywords", "roles", "permissions", "emails", "phones", "recipients",
        "authors", "labels", "aliases", "ids", "values", "items", "members",
    };

    /// <summary>Suffixes that suggest several values packed into one scalar column.</summary>
    public static readonly string[] MultiValueSuffixes =
        ["_list", "_csv", "_ids", "_tags", "_codes", "_emails", "_phones", "_names", "_values", "_array"];

    /// <summary>Data types that can hold a delimited list in a single scalar column.</summary>
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text", "character varying", "varchar", "character", "char", "nvarchar", "nchar", "ntext", "citext",
    };

    /// <summary>Numeric types eligible for the cached-aggregate heuristic.</summary>
    private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "smallint", "integer", "int", "bigint", "numeric", "decimal", "real", "double precision",
        "money", "smallmoney", "float", "tinyint", "int2", "int4", "int8",
    };

    /// <summary>Trailing-digit split used by the repeating-group rule: "phone2" → ("phone", 2).</summary>
    private static readonly Regex TrailingNumberRx =
        new(@"^(?<stem>.*?[^\d_])_?(?<num>\d{1,2})$", RegexOptions.Compiled);

    public static bool IsGeneric(string columnName) => GenericNames.Contains(columnName);

    public static bool IsTextType(string dataType) => TextTypes.Contains(dataType.Trim());

    public static bool IsNumericType(string dataType) => NumericTypes.Contains(dataType.Trim());

    public static bool LooksLikeAggregate(string columnName) =>
        AggregateExactNames.Contains(columnName)
        || AggregateSuffixes.Any(s => columnName.EndsWith(s, StringComparison.OrdinalIgnoreCase))
        || AggregatePrefixes.Any(p => columnName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static bool LooksLikeConcatenation(string columnName) => ConcatenatedNames.Contains(columnName);

    public static bool LooksLikeMultiValue(string columnName) =>
        MultiValueExactNames.Contains(columnName)
        || MultiValueSuffixes.Any(s => columnName.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Splits a trailing ordinal off a column name. Returns false when the name has no
    /// trailing number, or when the number is glued to a digit-bearing token (md5, sha256, x509)
    /// where the digits are part of the term rather than an ordinal.
    /// </summary>
    public static bool TrySplitOrdinal(string columnName, out string stem, out int ordinal)
    {
        stem = columnName;
        ordinal = 0;

        var m = TrailingNumberRx.Match(columnName);
        if (!m.Success) return false;

        stem = m.Groups["stem"].Value;
        if (stem.Length < 2) return false;

        return int.TryParse(m.Groups["num"].Value, out ordinal);
    }
}
