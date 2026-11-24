using DbLens.Core.Analysis.EfCore;

namespace DbLens.Docs.Inventory;

/// <summary>
/// The inventory of a solution, from files and EF models handed in. Pure: the same input gives the same
/// <see cref="ProjectInventory"/> whatever the order the files arrive in, and nothing is read, compiled or run.
/// </summary>
public static class ProjectInventoryEngine
{
    public static ProjectInventory Build(SolutionRead read) => Build(read.Files, read.Models, read.Coverage);

    public static ProjectInventory Build(
        IReadOnlyList<SourceFile> files, IReadOnlyList<EfModel> models, IReadOnlyList<CoverageNote>? reading = null)
    {
        var ordered = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var csharp = ordered.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).ToList();
        var tests = csharp.Where(f => IsTestPath(f.Path)).ToList();
        var parsed = csharp.Except(tests).Select(f => new ParsedFile(f)).ToList();

        // Two project files in one folder (Foo.csproj and Foo.Net48.csproj) share it: the first by path owns the files there.
        var projectDirectories = ordered
            .Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => DirectoryOf(f.Path), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => Path.GetFileNameWithoutExtension(g.First().Path), StringComparer.Ordinal);

        var projects = ProjectExtractor.Extract(ordered);
        var (entityItems, entityCoverage) = EntityExtractor.Extract(models, ordered);
        var endpoints = EndpointExtractor.Extract(parsed);
        var config = ConfigExtractor.Extract(parsed, ordered);
        var (clients, clientCoverage) = HttpClientExtractor.Extract(parsed, path => ProjectOf(path, projectDirectories));
        var hosted = HostedServiceExtractor.Extract(parsed);

        List<InventoryItem> items = [.. projects.Items, .. entityItems, .. EndpointItems(endpoints.Endpoints), .. config.Items, .. clients, .. hosted];

        List<CoverageNote> coverage = [.. reading ?? [], .. projects.Coverage, .. entityCoverage, .. config.Coverage, .. clientCoverage];
        var withoutRoute = endpoints.Endpoints.Count(e => e.Route is null);
        if (withoutRoute > 0)
            coverage.Add(new CoverageNote("endpoints", "Endpoints whose route is not written as text (conventional routing, a route built at run time, a group that cannot be followed).", withoutRoute));
        if (endpoints.NotInventoried.Count > 0)
            coverage.Add(new CoverageNote(
                "endpoints",
                "Map… calls that declare something this inventory does not list: "
                + string.Join(", ", endpoints.NotInventoried.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key} x{p.Value}")) + ".",
                endpoints.NotInventoried.Values.Sum()));
        if (tests.Count > 0)
            coverage.Add(new CoverageNote("source", "C# files in test folders were not read.", tests.Count));

        return new ProjectInventory(
            ProjectInventory.CurrentSchemaVersion,
            [.. items.OrderBy(i => i.Kind).ThenBy(i => i.Id, StringComparer.Ordinal)],
            [.. coverage.OrderBy(c => c.Area, StringComparer.Ordinal).ThenBy(c => c.Note, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// "endpoint:GET /a/b". Two declarations with the same verb and route (an API in two versions) get the
    /// handler's name added; the same handler twice, an ordinal. A route that is not text is keyed by its
    /// file and its handler.
    /// </summary>
    private static IEnumerable<InventoryItem> EndpointItems(IReadOnlyList<RawEndpoint> raws)
    {
        var ordered = raws.OrderBy(r => r.At.File, StringComparer.Ordinal).ThenBy(r => r.At.Line).ToList();
        var keyed = ordered.Select(r => (Raw: r, Base: r.Route is null ? $"endpoint:{r.Verb} ?@{r.At.File}" : $"endpoint:{r.Verb} {r.Route}")).ToList();
        var shared = keyed.GroupBy(k => k.Base, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

        // A route that is not text always carries its handler: otherwise adding a second action to the file would rename the first.
        var named = keyed.Select(k => (k.Raw, Id: (k.Raw.Route is null || shared.Contains(k.Base)) && k.Raw.Handler is not null ? $"{k.Base} [{k.Raw.Handler}]" : k.Base)).ToList();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var counts = named.GroupBy(n => n.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var (raw, id) in named)
        {
            var final = id;
            if (counts[id] > 1)
            {
                seen[id] = seen.GetValueOrDefault(id) + 1;
                final = $"{id} #{seen[id]}";
            }

            yield return new InventoryItem(
                final, ItemKind.Endpoint,
                raw.Route is null ? $"{raw.Verb} (route not written as text)" : $"{raw.Verb} {raw.Route}",
                raw.Route is null ? ItemEvidence.Unresolved : ItemEvidence.Extracted, raw.At,
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["handler"] = raw.Handler ?? "",
                    ["route"] = raw.Route ?? "",
                    ["style"] = raw.Style,
                    ["verb"] = raw.Verb,
                },
                [], raw.Reason);
        }
    }

    /// <summary>A path segment that names a test folder: such code is not the solution's own surface.</summary>
    internal static bool IsTestPath(string path)
    {
        var segments = path.Split('/');
        return segments.Take(segments.Length - 1).Any(s =>
            s.Equals("tests", StringComparison.OrdinalIgnoreCase) || s.Equals("test", StringComparison.OrdinalIgnoreCase)
            || s.Equals("e2e", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || s.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".FunctionalTests", StringComparison.OrdinalIgnoreCase)
            || s.EndsWith(".IntegrationTests", StringComparison.OrdinalIgnoreCase));
    }

    private static string DirectoryOf(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    // The project whose folder is the nearest at or above the file.
    private static string ProjectOf(string path, Dictionary<string, string> projectDirectories)
    {
        var directory = DirectoryOf(path);
        while (true)
        {
            if (projectDirectories.TryGetValue(directory, out var name)) return name;
            if (directory.Length == 0) return "(no project)";
            directory = DirectoryOf(directory);
        }
    }
}
