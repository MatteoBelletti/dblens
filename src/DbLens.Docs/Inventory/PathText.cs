namespace DbLens.Docs.Inventory;

/// <summary>Relative paths with '/', as the inventory keeps them.</summary>
internal static class PathText
{
    public static string Directory(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    /// <summary>"a/b" plus "../c/./d" is "a/c/d"; a leading "./" or "." is nothing.</summary>
    public static string Combine(string folder, string relative)
    {
        List<string> parts = [.. folder.Split('/', StringSplitOptions.RemoveEmptyEntries)];
        foreach (var part in relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (part != ".") parts.Add(part);
        return string.Join('/', parts);
    }
}
