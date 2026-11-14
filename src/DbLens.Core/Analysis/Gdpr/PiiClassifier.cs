using System.Text.RegularExpressions;

namespace DbLens.Core.Analysis.Gdpr;

/// <summary>
/// Matches a column name, reduced to lower snake case, against the PII pattern dictionaries.
/// A pattern marked qualifiable also matches inside a longer name ("customer_email"), at a lower
/// confidence; the others match the whole name only, their words being too common to trust in a
/// compound ("via", "pin", "condition").
/// </summary>
public static class PiiClassifier
{
    private const double QualifiedConfidenceFactor = 0.85;

    private static readonly Regex WordBoundaryRx =
        new(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

    private static readonly Regex SeparatorRx = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled);

    private static readonly Regex TrailingNumberRx = new(@"_?\d+$", RegexOptions.Compiled);

    // Words that turn a name into something about the value rather than the value itself:
    // is_email, send_email / email_verified, address_id, zip_file.
    private static readonly HashSet<string> LeadingStopWords =
        ["is", "has", "can", "allow", "send", "show", "hide", "require", "use", "enable", "notify", "valid", "verified"];

    private static readonly HashSet<string> TrailingStopWords =
    [
        "id", "ids", "count", "verified", "confirmed", "valid", "sent", "at", "on", "date", "type", "status",
        "flag", "enabled", "required", "template", "format", "length", "opt", "optin", "optout", "consent",
        "changed", "updated", "visible", "file",
    ];

    private static readonly Lazy<IReadOnlyList<(PiiPatternEntry Entry, Regex CompiledRegex)>> _compiled =
        new(CompileAll, LazyThreadSafetyMode.PublicationOnly);

    /// <summary>
    /// Returns the best match for the column, or null when none reaches <paramref name="minConfidence"/>.
    /// </summary>
    public static PiiMatch? Classify(string columnName, string sqlType, double minConfidence = 0.5)
    {
        var name = Normalize(columnName);
        var unnumbered = TrailingNumberRx.Replace(name, "");
        var parts = Parts(unnumbered).ToList();
        var typeLower = sqlType.ToLowerInvariant();

        PiiMatch? best = null;

        foreach (var (entry, rx) in _compiled.Value)
        {
            if (entry.SqlTypes.Length > 0 &&
                !entry.SqlTypes.Any(t => typeLower.StartsWith(t, StringComparison.Ordinal)))
                continue;

            var whole = rx.IsMatch(name) || rx.IsMatch(unnumbered);
            if (!whole && !(entry.Qualifiable && parts.Any(rx.IsMatch))) continue;

            var confidence = whole ? entry.Confidence : Math.Round(entry.Confidence * QualifiedConfidenceFactor, 2);
            if (confidence < minConfidence) continue;

            if (best is null || confidence > best.Confidence)
                best = new PiiMatch(columnName, entry.Category, entry.Name, confidence, Qualified: !whole);
        }

        return best;
    }

    /// <summary>The name in lower snake case: "First Name", "firstName" and "E-Mail" become first_name, e_mail.</summary>
    public static string Normalize(string columnName) =>
        SeparatorRx.Replace(WordBoundaryRx.Replace(columnName, "_"), "_").Trim('_').ToLowerInvariant();

    /// <summary>
    /// The name without its leading words (customer_email → email) and without its trailing ones
    /// (email_cliente → email), unless a dropped word says the column is not the value.
    /// </summary>
    private static IEnumerable<string> Parts(string name)
    {
        var words = name.Split('_', StringSplitOptions.RemoveEmptyEntries);

        for (var skip = 1; skip < words.Length; skip++)
            if (!words[..skip].Any(LeadingStopWords.Contains))
                yield return string.Join('_', words[skip..]);

        for (var take = words.Length - 1; take >= 1; take--)
            if (!words[take..].Any(TrailingStopWords.Contains))
                yield return string.Join('_', words[..take]);
    }

    private static IReadOnlyList<(PiiPatternEntry, Regex)> CompileAll() =>
        PiiPatternDictionary.All
            .Select(e => (e, new Regex(e.Regex, RegexOptions.Compiled | RegexOptions.IgnoreCase)))
            .ToList();
}
