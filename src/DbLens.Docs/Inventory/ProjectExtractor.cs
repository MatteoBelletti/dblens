using System.Xml;
using System.Xml.Linq;

namespace DbLens.Docs.Inventory;

internal sealed record ProjectExtraction(IReadOnlyList<InventoryItem> Items, IReadOnlyList<CoverageNote> Coverage);

/// <summary>
/// Projects from their <c>.csproj</c> as XML: SDK, output type, target frameworks, project references
/// and packages, with versions taken from the nearest <c>Directory.Packages.props</c> when the project
/// does not write them. No MSBuild evaluation runs, so a value built from a property stays unresolved.
/// </summary>
internal static class ProjectExtractor
{
    public static ProjectExtraction Extract(IReadOnlyList<SourceFile> files)
    {
        var paths = files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var props = files.Where(f => FileName(f.Path) == "Directory.Packages.props").ToDictionary(f => f.Path, f => Load(f.Text));
        var buildProps = files.Where(f => FileName(f.Path) == "Directory.Build.props").ToDictionary(f => f.Path, f => Load(f.Text));

        List<InventoryItem> items = [];
        var unparsed = 0;
        var unresolvedVersions = 0;
        var referencesOutside = 0;
        var noFramework = 0;
        Dictionary<string, List<string>> resolvedReferences = new(StringComparer.Ordinal);

        foreach (var file in files.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            if (Load(file.Text) is not { Root: { } project })
            {
                unparsed++;
                continue;
            }

            var name = Path.GetFileNameWithoutExtension(file.Path);
            var sdk = project.Attribute("Sdk")?.Value ?? "";

            var explicitOutput = project.Descendants("OutputType").FirstOrDefault()?.Value.Trim();
            var (output, outputFrom) = explicitOutput is { Length: > 0 }
                ? (explicitOutput, "file")
                : sdk is "Microsoft.NET.Sdk.Web" or "Microsoft.NET.Sdk.Worker" or "Microsoft.NET.Sdk.BlazorWebAssembly"
                    ? ("Exe", "sdk")
                    : ("Library", "default");

            var frameworks = (project.Descendants("TargetFramework").Concat(project.Descendants("TargetFrameworks")).FirstOrDefault()?.Value).NullIfBlank();
            var frameworksFrom = "file";
            if (frameworks is null)
            {
                frameworks = Inherited(file.Path, buildProps, "TargetFramework") ?? Inherited(file.Path, buildProps, "TargetFrameworks");
                frameworksFrom = "Directory.Build.props";
            }
            if (frameworks is null)
            {
                noFramework++;
                frameworks = "unknown";
                frameworksFrom = "none";
            }

            List<string> references = [];
            List<string> resolved = [];
            foreach (var reference in project.Descendants("ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (include is null) continue;
                var target = Resolve(file.Path, include);
                references.Add(Path.GetFileNameWithoutExtension(include.Replace('\\', '/')));
                if (include.Contains("$(", StringComparison.Ordinal) || !paths.Contains(target)) referencesOutside++;
                else resolved.Add(target);
            }

            resolvedReferences[file.Path] = resolved;

            List<string> packages = [];
            foreach (var package in project.Descendants("PackageReference"))
            {
                var id = package.Attribute("Include")?.Value; // an Update only changes a reference declared elsewhere
                if (id is null) continue;
                var version = (package.Attribute("Version")?.Value ?? package.Attribute("VersionOverride")?.Value ?? package.Element("Version")?.Value).NullIfBlank();
                version ??= CentralVersion(file.Path, id, props);
                if (version is null || version.Contains("$(", StringComparison.Ordinal))
                {
                    unresolvedVersions++;
                    packages.Add(id);
                }
                else packages.Add($"{id}@{version}");
            }

            items.Add(new InventoryItem(
                $"project:{name}", ItemKind.Project, name, ItemEvidence.Extracted, new SourceLocation(file.Path, 1),
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sdk"] = sdk,
                    ["outputType"] = output,
                    ["outputTypeFrom"] = outputFrom,
                    ["targetFrameworks"] = frameworks,
                    ["targetFrameworksFrom"] = frameworksFrom,
                    ["projectReferences"] = string.Join(", ", references.Order(StringComparer.Ordinal)),
                    ["packages"] = string.Join(", ", packages.Order(StringComparer.Ordinal)),
                },
                [], null));
        }

        // The same project name in two folders (src/Web and samples/Web): the folder tells them apart.
        var repeated = items.GroupBy(i => i.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        items = [.. items.Select(i => repeated.Contains(i.Name) ? i with { Id = $"project:{i.Name} [{(PathText.Directory(i.Source.File) is { Length: > 0 } folder ? folder : ".")}]" } : i)];

        // The names in projectReferences repeat when projects do: the ids say which project each one is.
        var idOfFile = items.ToDictionary(i => i.Source.File, i => i.Id, StringComparer.Ordinal);
        items = [.. items.Select(i =>
        {
            var attributes = new SortedDictionary<string, string>(i.Attributes.ToDictionary(a => a.Key, a => a.Value), StringComparer.Ordinal)
            {
                ["projectReferenceIds"] = string.Join(", ", (resolvedReferences.GetValueOrDefault(i.Source.File) ?? []).Where(idOfFile.ContainsKey).Select(f => idOfFile[f]).Order(StringComparer.Ordinal)),
            };
            return i with { Attributes = attributes };
        })];

        List<CoverageNote> coverage = [];
        if (items.Count == 0 && unparsed == 0) coverage.Add(new CoverageNote("projects", "No .csproj file was found under the root.", 0));
        if (unparsed > 0) coverage.Add(new CoverageNote("projects", "Project files that are not valid XML were skipped.", unparsed));
        if (unresolvedVersions > 0) coverage.Add(new CoverageNote("projects", "Package versions that come from an MSBuild property or are written nowhere in the files read are listed without a version.", unresolvedVersions));
        if (referencesOutside > 0) coverage.Add(new CoverageNote("projects", "Project references that point outside the files read, or are built from a property.", referencesOutside));
        if (noFramework > 0) coverage.Add(new CoverageNote("projects", "Projects whose target framework is written neither in the project nor in a Directory.Build.props.", noFramework));

        return new ProjectExtraction(items, coverage);
    }

    private static XDocument? Load(string text)
    {
        try
        {
            return XDocument.Parse(text);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string Resolve(string from, string relative) => PathText.Combine(PathText.Directory(from), relative);

    // The props files at or above the project's folder, nearest first.
    private static IEnumerable<KeyValuePair<string, XDocument?>> Above(string path, Dictionary<string, XDocument?> props)
    {
        var directory = PathText.Directory(path);
        while (true)
        {
            foreach (var pair in props.Where(p => PathText.Directory(p.Key) == directory))
                yield return pair;
            if (directory.Length == 0) yield break;
            directory = PathText.Directory(directory);
        }
    }

    private static string? Inherited(string path, Dictionary<string, XDocument?> buildProps, string element) =>
        Above(path, buildProps).Select(p => p.Value?.Descendants(element).FirstOrDefault()?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string? CentralVersion(string path, string id, Dictionary<string, XDocument?> props) =>
        Above(path, props)
            .Select(p => p.Value?.Descendants("PackageVersion").FirstOrDefault(v => string.Equals(v.Attribute("Include")?.Value, id, StringComparison.OrdinalIgnoreCase))
                ?.Attribute("Version")?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}

internal static class StringExtensions
{
    public static string? NullIfBlank(this string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
