using System.Text.Json;

namespace DbLens.Docs.Documents;

/// <summary>
/// The documents on disk. It reads and writes only the known Markdown files of the docs folder, which must
/// lie inside the root: a folder setting that climbs out, or is absolute, is refused.
/// </summary>
public static class DocsFolder
{
    public static IReadOnlyDictionary<string, string> Read(string root, DocsOptions options)
    {
        var folder = Resolve(root, options);
        Dictionary<string, string> documents = new(StringComparer.Ordinal);
        foreach (var file in DocPlan.Files)
        {
            var path = Path.Combine(folder, file);
            if (File.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                documents[$"{options.Folder}/{file}"] = File.ReadAllText(path);
        }

        return documents;
    }

    public static void Write(string root, DocsOptions options, IReadOnlyDictionary<string, string> files)
    {
        var folder = Resolve(root, options);
        Directory.CreateDirectory(folder);
        foreach (var (path, text) in files)
        {
            var name = path[(options.Folder.Length + 1)..];
            if (!DocPlan.Files.Contains(name, StringComparer.Ordinal)) throw new ArgumentException($"{path} is not one of the project documents.");

            var target = Path.Combine(folder, name);
            if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException($"{path} is a link and was not written.");

            // A name nobody could have planted: CreateNew fails on anything that already exists, a link included.
            var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                writer.Write(text);
            File.Move(temporary, target, overwrite: true);
        }
    }

    /// <summary>The full path of the docs folder, inside the root.</summary>
    public static string Resolve(string root, DocsOptions options)
    {
        var folder = options.Folder;
        if (folder.Length == 0 || Path.IsPathRooted(folder) || folder.Split('/').Contains(".."))
            throw new ArgumentException("The docs folder must be a relative path inside the root.");

        var full = Path.GetFullPath(Path.Combine(root, folder));
        var inside = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(inside, StringComparison.Ordinal)) throw new ArgumentException("The docs folder must be inside the root.");

        // A folder that is a link leads wherever it points: neither it nor any folder above it, below the root, may be one.
        var walk = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var segment in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            walk = Path.Combine(walk, segment);
            if (Directory.Exists(walk) && new DirectoryInfo(walk).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException($"{segment} in the docs folder is a link: the documents are not written through links.");
        }

        return full;
    }
}

/// <summary>The optional <c>dblens-docs.json</c> at the root: <c>{ "language": "it", "docs": "docs/project" }</c>.</summary>
public static class DocsConfig
{
    public const string FileName = "dblens-docs.json";

    public static DocsOptions Load(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path)) return new DocsOptions();

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{FileName} must hold a JSON object.");

            var defaults = new DocsOptions();
            return new DocsOptions(Text(document.RootElement, "language") ?? defaults.Language, Text(document.RootElement, "docs") ?? defaults.Directory);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{FileName} is not valid JSON: {ex.Message}");
        }
    }

    private static string? Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : null
            : throw new ArgumentException($"{FileName}: \"{name}\" must be text.");
    }
}
