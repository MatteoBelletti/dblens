using System.Security.Cryptography;
using System.Text;
using DbLens.Docs.Inventory;

namespace DbLens.Docs.Documents;

/// <summary>Short stable hashes: of an inventory item (what a slot is written about) and of text (what a region held).</summary>
public static class ItemHash
{
    /// <summary>
    /// What the item says, not where it sits: a line number is left out, so an unrelated edit above a
    /// declaration does not make the text written about it stale.
    /// </summary>
    // Attributes no document shows: a change in them must not make the words written about the item stale.
    private static readonly HashSet<string> NotShown = new(StringComparer.Ordinal) { "projectReferenceIds" };

    public static string Of(InventoryItem item)
    {
        var text = new StringBuilder()
            .Append(item.Kind).Append('|').Append(item.Id).Append('|').Append(item.Name).Append('|')
            .Append(item.Evidence).Append('|').Append(item.Reason).Append('|');
        foreach (var (key, value) in item.Attributes.Where(a => !NotShown.Contains(a.Key)).OrderBy(a => a.Key, StringComparer.Ordinal))
            text.Append(key).Append('=').Append(value).Append(';');
        return OfText(text.ToString());
    }

    /// <summary>Eight hex digits of the text's SHA-256, after line endings are made LF and trailing blanks trimmed.</summary>
    public static string OfText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").TrimEnd())))[..8].ToLowerInvariant();
}
