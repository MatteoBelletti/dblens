using DbLens.Core.Analysis.EfCore;
using DbLens.EfCore;

namespace DbLens.Docs.Inventory;

public sealed record SolutionRead(
    IReadOnlyList<SourceFile> Files,
    IReadOnlyList<EfModel> Models,
    /// <summary>What reading the folder could not do: a file too large, one unreadable, a snapshot that did not parse.</summary>
    IReadOnlyList<CoverageNote> Coverage)
{
    /// <summary>The snapshot file each model was read from, in the order of <see cref="Models"/>. Empty when the models were handed in without one.</summary>
    public IReadOnlyList<string> ModelSources { get; init; } = [];
}

/// <summary>
/// The one place that touches a folder. It opens only <c>*.cs</c>, <c>*.csproj</c>, <c>Directory.Build.props</c>,
/// <c>Directory.Packages.props</c> and <c>appsettings*.json</c>, skips build output and dependency folders,
/// does not follow links, and reads the EF Core snapshots through <see cref="SnapshotReader"/>. Nothing is
/// compiled, loaded or run.
/// </summary>
public static class SolutionReader
{
    public const long MaxFileBytes = 2 * 1024 * 1024;

    public const int MaxFiles = 50_000;

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".vscode",
    };

    public static SolutionRead Read(string root, int maxFiles = MaxFiles)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new ArgumentException("root must be an existing folder.");

        var full = Path.GetFullPath(root);
        List<SourceFile> files = [];
        List<CoverageNote> coverage = [];
        var tooLarge = 0;
        var unreadable = 0;
        var skippedByCap = 0;

        // Candidates first, ordered, then the cap: which files are kept must not depend on the file system's enumeration order.
        List<string> candidates = [];
        Stack<string> pending = new([full]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(directory))
                    if (!SkippedFolders.Contains(Path.GetFileName(sub)) && !IsLink(sub)) pending.Push(sub);

                candidates.AddRange(Directory.EnumerateFiles(directory).Where(IsRead).Where(p => !IsLink(p)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable++;
            }
        }

        // In path order, so which files fit does not depend on the file system; a file that is too large does not use a place.
        var overLimit = false;
        foreach (var path in candidates.OrderBy(p => Path.GetRelativePath(full, p).Replace('\\', '/'), StringComparer.Ordinal))
        {
            if (files.Count >= maxFiles)
            {
                overLimit = true;
                skippedByCap++;
                continue;
            }

            try
            {
                if (new FileInfo(path).Length > MaxFileBytes)
                {
                    tooLarge++;
                    continue;
                }

                files.Add(new SourceFile(Path.GetRelativePath(full, path).Replace('\\', '/'), File.ReadAllText(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable++;
            }
        }

        if (tooLarge > 0) coverage.Add(new CoverageNote("reading", $"Files over {MaxFileBytes / (1024 * 1024)} MB were not read.", tooLarge));
        if (unreadable > 0) coverage.Add(new CoverageNote("reading", "Folders or files that could not be read.", unreadable));
        if (overLimit) coverage.Add(new CoverageNote("reading", $"Only the first {maxFiles} files, by path, were read.", skippedByCap));

        List<EfModel> models = [];
        List<string> modelSources = [];
        foreach (var snapshot in files.Where(f => f.Path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            try
            {
                models.Add(SnapshotReader.Read(Path.Combine(full, snapshot.Path)));
                modelSources.Add(snapshot.Path);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                coverage.Add(new CoverageNote("data model", $"The EF Core snapshot {snapshot.Path} was not read: {ex.Message}", 1));
            }
        }

        return new SolutionRead([.. files.OrderBy(f => f.Path, StringComparer.Ordinal)], models, coverage) { ModelSources = modelSources };
    }

    private static bool IsRead(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
               || ComposeReader.IsCompose(name)
               || (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
}
