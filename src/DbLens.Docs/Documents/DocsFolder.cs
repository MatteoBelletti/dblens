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

        // Every file is written under a temporary name first, and only then moved into place: a failure while
        // writing leaves every document as it was; one while moving leaves the earlier ones replaced.
        List<(string Temporary, string Target)> staged = [];
        try
        {
            foreach (var (path, text) in files)
            {
                if (!path.StartsWith(options.Folder + "/", StringComparison.Ordinal)) throw new ArgumentException($"{path} is not in the docs folder {options.Folder}.");
                var name = path[(options.Folder.Length + 1)..];
                if (!DocPlan.Files.Contains(name, StringComparer.Ordinal)) throw new ArgumentException($"{path} is not one of the project documents.");

                var target = Path.Combine(folder, name);
                if (File.Exists(target) && File.GetAttributes(target).HasFlag(FileAttributes.ReparsePoint))
                    throw new ArgumentException($"{path} is a link and was not written.");

                // A name nobody could have planted: CreateNew fails on anything that already exists, a link included.
                var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
                staged.Add((temporary, target));
                using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                writer.Write(text);
            }

            foreach (var (temporary, target) in staged) File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            foreach (var (temporary, _) in staged)
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // The error that matters is the one being thrown.
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> while holding the lock of this docs folder, which every process that reads
    /// the documents in order to write them takes (the command line and the MCP server): two of them never read
    /// the same text and write one after the other, losing the first's. The lock file is in the temporary folder, not in the repository.
    /// </summary>
    public static T Locked<T>(string root, DocsOptions options, Func<T> work, TimeSpan? wait = null)
    {
        var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Resolve(root, options))))[..16];
        var path = Path.Combine(Path.GetTempPath(), $"dblens-docs-{name}.lock");
        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(30));

        while (true)
        {
            FileStream? held = null;
            try
            {
                held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(50);
                continue;
            }
            catch (IOException)
            {
                throw new IOException("Another dblens-docs is writing these documents: try again in a moment.");
            }

            using (held) return work();
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
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : null
            : throw new ArgumentException($"{FileName}: \"{name}\" must be text.");
    }
}
