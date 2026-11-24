using System.Text.Json;
using DbLens.Docs.Inventory;

namespace DbLens.Docs.Documents;

/// <summary>
/// The documents on disk. It reads and writes only the known Markdown files of the docs folder, which must
/// lie inside the root: a folder setting that climbs out, or is absolute, is refused.
/// </summary>
public static class DocsFolder
{
    private const int MaxDetailDocuments = 200;

    public static IReadOnlyDictionary<string, string> Read(string root, DocsOptions options)
    {
        var folder = Resolve(root, options);
        Dictionary<string, string> documents = new(StringComparer.Ordinal);
        foreach (var file in DocPlan.Files)
        {
            var path = Path.Combine(folder, file);
            if (File.Exists(path) && !IsLink(path))
                documents[$"{options.Folder}/{file}"] = File.ReadAllText(path);
        }

        var details = Path.Combine(folder, DocPlan.DetailFolder);
        if (Directory.Exists(details) && !IsLink(details))
            foreach (var path in Directory.GetFiles(details, "*.md").Order(StringComparer.Ordinal).Take(MaxDetailDocuments))
            {
                var file = $"{DocPlan.DetailFolder}/{Path.GetFileName(path)}";
                if (DocPlan.IsDetailFile(file) && !IsLink(path))
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
                if (!DocPlan.Files.Contains(name, StringComparer.Ordinal) && !DocPlan.IsDetailFile(name)) throw new ArgumentException($"{path} is not one of the project documents.");

                var target = Path.Combine(folder, name);
                if (DocPlan.IsDetailFile(name))
                {
                    var details = Path.GetDirectoryName(target)!;
                    if (Directory.Exists(details) && IsLink(details))
                        throw new ArgumentException($"{DocPlan.DetailFolder} is a link: the documents are not written through links.");
                    Directory.CreateDirectory(details);
                }

                if (File.Exists(target) && IsLink(target))
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

    /// <summary>Scaffolds the documents under the lock: reads them, writes the result, and starts the detail documents of <paramref name="newDetails"/>.</summary>
    public static ScaffoldResult Scaffold(string root, DocsOptions options, ProjectInventory inventory, IEnumerable<string>? newDetails = null) =>
        Locked(root, options, () =>
        {
            var scaffolded = Scaffolder.Scaffold(inventory, Read(root, options), options, newDetails);
            Write(root, options, scaffolded.Files);
            return scaffolded;
        });

    internal static bool IsLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

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
            if (Directory.Exists(walk) && IsLink(walk))
                throw new ArgumentException($"{segment} in the docs folder is a link: the documents are not written through links.");
        }

        return full;
    }
}

/// <summary>The optional <c>dblens-docs.json</c> at the root: <c>{ "language": "it", "docs": "docs/project" }</c>.</summary>
public static class DocsConfig
{
    public const string FileName = "dblens-docs.json";

    public static DocsOptions Load(string root) => Read(root, new DocsOptions(), config =>
    {
        var defaults = new DocsOptions();
        return new DocsOptions(Text(config, "language") ?? defaults.Language, Text(config, "docs") ?? defaults.Directory);
    });

    /// <summary>
    /// What the file adds to the inventory's defaults: <c>"events": { "bases": [...], "producingMethods": [...] }</c> and
    /// <c>"components": { "Account": ["Account/Command", "Account/Query"] }</c> (components joined under one name).
    /// </summary>
    public static InventoryOptions LoadInventory(string root) => Read(root, InventoryOptions.Default, config =>
    {
        var defaults = InventoryOptions.Default;
        List<string> bases = [.. defaults.EventBases];
        List<string> producing = [.. defaults.ProducingMethods];
        Dictionary<string, IReadOnlyList<string>> merge = new(StringComparer.Ordinal);

        if (config.TryGetProperty("events", out var events) && events.ValueKind != JsonValueKind.Null)
        {
            if (events.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{FileName}: \"events\" must be an object.");
            bases.AddRange(Strings(events, "events", "bases"));
            producing.AddRange(Strings(events, "events", "producingMethods"));
        }

        if (config.TryGetProperty("components", out var components) && components.ValueKind != JsonValueKind.Null)
        {
            if (components.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{FileName}: \"components\" must be an object of name to list of names.");
            foreach (var property in components.EnumerateObject())
                merge[property.Name] = property.Value.ValueKind == JsonValueKind.Array && property.Value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String)
                    ? [.. property.Value.EnumerateArray().Select(v => v.GetString()!)]
                    : throw new ArgumentException($"{FileName}: \"components\".\"{property.Name}\" must be a list of names.");
        }

        return new InventoryOptions { EventBases = [.. bases.Distinct(StringComparer.Ordinal)], ProducingMethods = [.. producing.Distinct(StringComparer.Ordinal)], Merge = merge };
    });

    private static T Read<T>(string root, T whenMissing, Func<JsonElement, T> build)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path)) return whenMissing;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException($"{FileName} must hold a JSON object.");
            return build(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{FileName} is not valid JSON: {ex.Message}");
        }
    }

    private static IEnumerable<string> Strings(JsonElement section, string sectionName, string name)
    {
        if (!section.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return [];
        return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String)
            ? value.EnumerateArray().Select(v => v.GetString()!)
            : throw new ArgumentException($"{FileName}: \"{sectionName}\".\"{name}\" must be a list of text.");
    }

    private static string? Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : null
            : throw new ArgumentException($"{FileName}: \"{name}\" must be text.");
    }
}
