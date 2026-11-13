using System.Text.RegularExpressions;

namespace DbLens.Core.Analysis.Relationships;

/// <summary>
/// Recognizes column names that look like references, and table names they could refer to.
/// Covers English and Italian conventions, because the second is what an Italian ERP uses:
/// <c>customer_id</c>, <c>id_cliente</c>, <c>cod_cliente</c>, <c>customerId</c>.
/// </summary>
public static class ReferenceNaming
{
    public enum KeyFamily
    {
        /// <summary>A surrogate key: <c>x_id</c>, <c>id_x</c>, <c>xId</c>.</summary>
        Id,
        /// <summary>A business code: <c>cod_x</c>, <c>x_cod</c>, <c>codice_x</c>, <c>x_code</c>.</summary>
        Code,
    }

    public sealed record ReferenceStem(string Stem, KeyFamily Family);

    private static readonly Regex SuffixId = new(@"^(?<stem>[a-z][a-z0-9_]*?)_id$", RegexOptions.Compiled);
    private static readonly Regex PrefixId = new(@"^id_(?<stem>[a-z][a-z0-9_]*)$", RegexOptions.Compiled);
    private static readonly Regex CamelId = new(@"^(?<stem>[a-z][a-zA-Z0-9]*?[a-z0-9])Id$", RegexOptions.Compiled);
    private static readonly Regex SuffixCode = new(@"^(?<stem>[a-z][a-z0-9_]*?)_(?:cod|code|codice)$", RegexOptions.Compiled);
    private static readonly Regex PrefixCode = new(@"^(?:cod|codice|code)_(?<stem>[a-z][a-z0-9_]*)$", RegexOptions.Compiled);

    /// <summary>The referenced-entity stem a column name points at, or null when it does not look like a reference.</summary>
    public static ReferenceStem? StemOf(string columnName)
    {
        if (CamelId.Match(columnName) is { Success: true } camel)
            return new(camel.Groups["stem"].Value.ToLowerInvariant(), KeyFamily.Id);

        var name = columnName.ToLowerInvariant();

        if (SuffixId.Match(name) is { Success: true } a) return new(a.Groups["stem"].Value, KeyFamily.Id);
        if (PrefixId.Match(name) is { Success: true } b) return new(b.Groups["stem"].Value, KeyFamily.Id);
        if (SuffixCode.Match(name) is { Success: true } c) return new(c.Groups["stem"].Value, KeyFamily.Code);
        if (PrefixCode.Match(name) is { Success: true } d) return new(d.Groups["stem"].Value, KeyFamily.Code);

        return null;
    }

    /// <summary>
    /// True when a table name plausibly names the entity a stem refers to: the same word, or its
    /// English or Italian plural (order/orders, category/categories, cliente/clienti,
    /// articolo/articoli, fattura/fatture).
    /// </summary>
    public static bool TableMatchesStem(string tableName, string stem)
    {
        var table = tableName.ToLowerInvariant();
        return Forms(stem).Contains(table);
    }

    private static HashSet<string> Forms(string stem)
    {
        var forms = new HashSet<string>(StringComparer.Ordinal) { stem, stem + "s", stem + "es" };

        if (stem.EndsWith('y') && stem.Length > 1) forms.Add(stem[..^1] + "ies");

        // Italian plurals: -o and -e become -i, -a becomes -e.
        if (stem.Length > 2)
        {
            var last = stem[^1];
            var body = stem[..^1];
            if (last is 'o' or 'e') forms.Add(body + "i");
            if (last == 'a') forms.Add(body + "e");
        }

        return forms;
    }
}
