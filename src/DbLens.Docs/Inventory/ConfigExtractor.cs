using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbLens.Docs.Inventory;

internal sealed record ConfigExtraction(IReadOnlyList<InventoryItem> Items, IReadOnlyList<CoverageNote> Coverage);

/// <summary>
/// Configuration keys, from the code that reads them (<c>GetSection</c>, <c>GetValue</c>,
/// <c>GetConnectionString</c>, an indexer on a configuration) and from the key names of
/// <c>appsettings*.json</c>. Only key names are kept: a value in a settings file may be a secret and is
/// never read into the inventory.
/// </summary>
internal static class ConfigExtractor
{
    private sealed record Seen(string Key, SourceLocation At, string From);

    public static ConfigExtraction Extract(IReadOnlyList<ParsedFile> code, IReadOnlyList<SourceFile> settings)
    {
        List<Seen> seen = [];
        var computed = 0;

        foreach (var file in code)
        {
            var constants = Constants.Of(file);
            foreach (var node in file.Root.DescendantNodes())
            {
                switch (node)
                {
                    case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } call
                        when Read(call, access, constants, out var key, out var isComputed):
                        if (isComputed) computed++;
                        else seen.Add(new Seen(key!, file.Locate(call), "code"));
                        break;

                    case ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } indexer
                        when SectionPath(indexer.Expression, constants) is { } prefix:
                        var text = RouteText.Of(indexer.ArgumentList.Arguments[0].Expression, constants, out _);
                        if (text is null) computed++;
                        else seen.Add(new Seen(Combine(prefix, text), file.Locate(indexer), "code"));
                        break;
                }
            }
        }

        foreach (var file in settings.Where(IsSettings))
            foreach (var (key, line) in JsonKeys(file.Text))
                seen.Add(new Seen(key, new SourceLocation(file.Path, line), "json"));

        List<InventoryItem> items = [];
        foreach (var group in seen
                     .OrderBy(s => s.At.File, StringComparer.Ordinal).ThenBy(s => s.At.Line)
                     .GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            items.Add(new InventoryItem(
                $"config:{first.Key}", ItemKind.Config, first.Key, ItemEvidence.Extracted, first.At,
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["readIn"] = string.Join(", ", group.Select(s => s.From).Distinct().Order(StringComparer.Ordinal)),
                },
                [.. group.Skip(1).Select(s => s.At).Distinct()], null));
        }

        List<CoverageNote> coverage = [];
        if (computed > 0)
            coverage.Add(new CoverageNote("configuration", "Configuration reads whose key is not written as text (a variable, a call, a value built at run time).", computed));
        return new ConfigExtraction(items, coverage);
    }

    /// <summary>The configuration key a code expression reads, when it is a read written as text: used for the base address of a client.</summary>
    public static string? KeyOfRead(ExpressionSyntax? e, IReadOnlyDictionary<string, string> constants)
    {
        switch (e)
        {
            case ElementAccessExpressionSyntax { ArgumentList.Arguments.Count: 1 } indexer when SectionPath(indexer.Expression, constants) is { } prefix:
                return RouteText.Of(indexer.ArgumentList.Arguments[0].Expression, constants, out _) is { } text ? Combine(prefix, text) : null;
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } call when Read(call, access, constants, out var key, out var isComputed) && !isComputed:
                return key;
            default:
                return null;
        }
    }

    // Reads of a key through a method: GetSection, GetRequiredSection, GetValue, GetConnectionString.
    private static bool Read(
        InvocationExpressionSyntax call, MemberAccessExpressionSyntax access, IReadOnlyDictionary<string, string> constants,
        out string? key, out bool computed)
    {
        key = null;
        computed = false;
        var name = access.Name.Identifier.Text;
        if (name is not ("GetSection" or "GetRequiredSection" or "GetValue" or "GetConnectionString")) return false;
        if (call.ArgumentList.Arguments.Count == 0) return false;

        var prefix = name == "GetConnectionString" ? SectionPath(access.Expression, constants) is null ? null : "" : SectionPath(access.Expression, constants);
        if (prefix is null) return false;

        var text = RouteText.Of(call.ArgumentList.Arguments[0].Expression, constants, out _);
        if (text is null)
        {
            computed = true;
            return true;
        }

        key = name == "GetConnectionString" ? Combine("ConnectionStrings", text) : Combine(prefix, text);
        return true;
    }

    /// <summary>
    /// "" for a configuration itself (an identifier or member named like one), "A:B" for a section reached
    /// with GetSection("A").GetSection("B"); null for anything else.
    /// </summary>
    private static string? SectionPath(ExpressionSyntax e, IReadOnlyDictionary<string, string> constants)
    {
        switch (e)
        {
            case IdentifierNameSyntax id: return LooksLikeConfiguration(id.Identifier.Text) ? "" : null;
            case MemberAccessExpressionSyntax m when m.Name.Identifier.Text is not ("GetSection" or "GetRequiredSection"):
                return LooksLikeConfiguration(m.Name.Identifier.Text) ? "" : null;
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } call
                when access.Name.Identifier.Text is "GetSection" or "GetRequiredSection" && call.ArgumentList.Arguments.Count > 0:
                var up = SectionPath(access.Expression, constants);
                var text = RouteText.Of(call.ArgumentList.Arguments[0].Expression, constants, out _);
                return up is null || text is null ? null : Combine(up, text);
            case ParenthesizedExpressionSyntax p: return SectionPath(p.Expression, constants);
            default: return null;
        }
    }

    private static bool LooksLikeConfiguration(string name) =>
        name.Contains("onfiguration", StringComparison.Ordinal)
        || (name.Contains("onfig", StringComparison.Ordinal) && !name.Contains("onfigure", StringComparison.Ordinal));

    private static string Combine(string prefix, string key) => prefix.Length == 0 ? key : prefix + ":" + key;

    private static bool IsSettings(SourceFile file)
    {
        var name = file.Path[(file.Path.LastIndexOf('/') + 1)..];
        return name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Frame(bool isArray, bool pushed)
    {
        public bool IsArray { get; } = isArray;

        public bool Pushed { get; } = pushed;

        public int Next { get; set; }
    }

    /// <summary>The leaf keys of a settings file with their lines, as "A:B:C" (array items as "A:0"). Values are not read out.</summary>
    private static IEnumerable<(string Key, int Line)> JsonKeys(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var lineStarts = LineStarts(bytes);
        List<(string Key, int Line)> keys = [];
        List<string> path = [];
        List<Frame> frames = [];
        string? pending = null;
        var pendingLine = 0;

        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            while (reader.Read())
            {
                var token = reader.TokenType;
                if (token == JsonTokenType.PropertyName)
                {
                    pending = reader.GetString();
                    pendingLine = LineOf(lineStarts, (int)reader.TokenStartIndex);
                    continue;
                }

                if (token is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    if (frames[^1].Pushed) path.RemoveAt(path.Count - 1);
                    frames.RemoveAt(frames.Count - 1);
                    continue;
                }

                // A value begins: an object, an array or a leaf. Its segment is the property name or the array index.
                string? segment = null;
                var line = LineOf(lineStarts, (int)reader.TokenStartIndex);
                if (frames.Count > 0)
                {
                    if (frames[^1].IsArray) segment = (frames[^1].Next++).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    else
                    {
                        segment = pending;
                        line = pendingLine;
                        pending = null;
                    }
                }

                if (token is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    frames.Add(new Frame(token == JsonTokenType.StartArray, segment is not null));
                    if (segment is not null) path.Add(segment);
                }
                else if (segment is not null) keys.Add((string.Join(':', [.. path, segment]), line));
            }
        }
        catch (JsonException)
        {
            // A settings file that is not valid JSON yields the keys read before the error.
        }

        return keys;
    }

    private static List<int> LineStarts(byte[] bytes)
    {
        List<int> starts = [0];
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] == (byte)'\n') starts.Add(i + 1);
        return starts;
    }

    private static int LineOf(List<int> starts, int offset)
    {
        var index = starts.BinarySearch(offset);
        return (index >= 0 ? index : ~index - 1) + 1;
    }
}
