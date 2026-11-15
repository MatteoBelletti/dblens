using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbLens.Core.Analysis.Gdpr;

internal sealed record PiiPatternEntry(
    string Name,
    PiiCategory Category,
    string Regex,
    string[] SqlTypes,
    double Confidence,
    bool Qualifiable);

internal static class PiiPatternDictionary
{
    private static readonly Lazy<IReadOnlyList<PiiPatternEntry>> _all =
        new(LoadAll, LazyThreadSafetyMode.PublicationOnly);

    public static IReadOnlyList<PiiPatternEntry> All => _all.Value;

    private static IReadOnlyList<PiiPatternEntry> LoadAll()
    {
        var assembly = typeof(PiiPatternDictionary).Assembly;
        List<PiiPatternEntry> list = [];

        foreach (var resource in new[] { "pii_patterns_en.json", "pii_patterns_it.json" })
        {
            var resourceName = $"DbLens.Core.Analysis.Gdpr.Patterns.{resource}";
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");

            var doc = JsonDocument.Parse(stream);
            foreach (var element in doc.RootElement.GetProperty("patterns").EnumerateArray())
            {
                var name = element.GetProperty("name").GetString()!;
                var categoryStr = element.GetProperty("category").GetString()!;
                var regex = element.GetProperty("regex").GetString()!;
                var confidence = element.GetProperty("confidence").GetDouble();

                var sqlTypes = element.GetProperty("sqlTypes").EnumerateArray()
                    .Select(e => e.GetString()!)
                    .ToArray();

                if (!Enum.TryParse<PiiCategory>(categoryStr, out var category))
                    throw new InvalidOperationException(
                        $"Unknown PII category '{categoryStr}' in pattern '{name}'.");

                var qualifiable = element.TryGetProperty("qualifiable", out var q) && q.GetBoolean();

                list.Add(new PiiPatternEntry(name, category, regex, sqlTypes, confidence, qualifiable));
            }
        }

        return list;
    }
}
