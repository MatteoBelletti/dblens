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

            var temporary = target + ".tmp";
            File.WriteAllText(temporary, text);
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
        return full.StartsWith(inside, StringComparison.Ordinal) ? full : throw new ArgumentException("The docs folder must be inside the root.");
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
            var defaults = new DocsOptions();
            return new DocsOptions(
                document.RootElement.TryGetProperty("language", out var language) && language.GetString() is { Length: > 0 } l ? l : defaults.Language,
                document.RootElement.TryGetProperty("docs", out var docs) && docs.GetString() is { Length: > 0 } d ? d : defaults.Directory);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{FileName} is not valid JSON: {ex.Message}");
        }
    }
}
