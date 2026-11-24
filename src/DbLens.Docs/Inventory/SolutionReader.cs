using DbLens.Core.Analysis.EfCore;
using DbLens.EfCore;

namespace DbLens.Docs.Inventory;

public sealed record SolutionRead(
    IReadOnlyList<SourceFile> Files,
    IReadOnlyList<EfModel> Models,
    /// <summary>What reading the folder could not do: a file too large, one unreadable, a snapshot that did not parse.</summary>
    IReadOnlyList<CoverageNote> Coverage);

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

    public static SolutionRead Read(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new ArgumentException("root must be an existing folder.");

        var full = Path.GetFullPath(root);
        List<SourceFile> files = [];
        List<CoverageNote> coverage = [];
        var tooLarge = 0;
        var unreadable = 0;
        var overLimit = false;

        Stack<string> pending = new([full]);
        while (pending.Count > 0 && !overLimit)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(directory))
                    if (!SkippedFolders.Contains(Path.GetFileName(sub)) && !IsLink(sub)) pending.Push(sub);

                foreach (var path in Directory.EnumerateFiles(directory).Where(IsRead).Where(p => !IsLink(p)))
                {
                    if (files.Count >= MaxFiles)
                    {
                        overLimit = true;
                        break;
                    }

                    if (new FileInfo(path).Length > MaxFileBytes)
                    {
                        tooLarge++;
                        continue;
                    }

                    files.Add(new SourceFile(Path.GetRelativePath(full, path).Replace('\\', '/'), File.ReadAllText(path)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unreadable++;
            }
        }

        if (tooLarge > 0) coverage.Add(new CoverageNote("reading", $"Files over {MaxFileBytes / (1024 * 1024)} MB were not read.", tooLarge));
        if (unreadable > 0) coverage.Add(new CoverageNote("reading", "Folders or files that could not be read.", unreadable));
        if (overLimit) coverage.Add(new CoverageNote("reading", $"Only the first {MaxFiles} files were read.", MaxFiles));

        List<EfModel> models = [];
        foreach (var snapshot in files.Where(f => f.Path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            try
            {
                models.Add(SnapshotReader.Read(Path.Combine(full, snapshot.Path)));
            }
            catch (ArgumentException ex)
            {
                coverage.Add(new CoverageNote("data model", $"The EF Core snapshot {snapshot.Path} was not read: {ex.Message}", 1));
            }
        }

        return new SolutionRead([.. files.OrderBy(f => f.Path, StringComparer.Ordinal)], models, coverage);
    }

    private static bool IsRead(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
               || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
               || (name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
}
