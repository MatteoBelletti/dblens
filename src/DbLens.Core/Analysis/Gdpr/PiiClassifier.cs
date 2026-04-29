using System.Text.RegularExpressions;

namespace DbLens.Core.Analysis.Gdpr;

public static class PiiClassifier
{
    private static readonly Lazy<IReadOnlyList<(PiiPatternEntry Entry, Regex CompiledRegex)>> _compiled =
        new(CompileAll, LazyThreadSafetyMode.PublicationOnly);

    public static PiiMatch? Classify(string columnName, string sqlType, double minConfidence = 0.5)
    {
        var lower = columnName.ToLowerInvariant();
        var typeLower = sqlType.ToLowerInvariant();

        PiiMatch? best = null;

        foreach (var (entry, rx) in _compiled.Value)
        {
            if (entry.Confidence < minConfidence)
                continue;

            if (!rx.IsMatch(lower))
                continue;

            if (entry.SqlTypes.Length > 0 &&
                !entry.SqlTypes.Any(t => typeLower.StartsWith(t, StringComparison.Ordinal)))
                continue;

            if (best is null || entry.Confidence > best.Confidence)
                best = new PiiMatch(columnName, entry.Category, entry.Name, entry.Confidence);
        }

        return best;
    }

    private static IReadOnlyList<(PiiPatternEntry, Regex)> CompileAll() =>
        PiiPatternDictionary.All
            .Select(e => (e, new Regex(e.Regex, RegexOptions.Compiled | RegexOptions.IgnoreCase)))
            .ToList();
}
