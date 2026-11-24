namespace DbLens.Docs.Inventory;

/// <param name="Role"><c>service</c> (a web or worker executable), <c>application</c> (any other executable) or <c>shared-library</c>.</param>
/// <param name="UsedBy">For a shared library: the components that use it. Empty for the others.</param>
internal sealed record ComponentInfo(
    string Name, string Role, IReadOnlyList<string> Executables, IReadOnlyList<string> Libraries, IReadOnlyList<string> UsedBy, string Sdk);

/// <summary>
/// The macro-components of a solution, from its projects alone. A component is an executable with the libraries
/// only it uses; a library that several executables use is a component of its own, with the list of its users.
/// The orchestrator (an Aspire AppHost) and the test projects are not components. Pure.
/// </summary>
internal sealed class ComponentModel
{
    private readonly Dictionary<string, string> _componentOfProject;
    private readonly List<(string Directory, string ProjectId)> _directories;

    private ComponentModel(
        IReadOnlyList<ComponentInfo> components, Dictionary<string, string> componentOfProject,
        List<(string, string)> directories, IReadOnlyList<string> orchestrators, int unused)
    {
        Components = components;
        Orchestrators = orchestrators;
        UnusedLibraries = unused;
        _componentOfProject = componentOfProject;
        _directories = directories;
    }

    public IReadOnlyList<ComponentInfo> Components { get; }

    /// <summary>Project ids of the orchestrators.</summary>
    public IReadOnlyList<string> Orchestrators { get; }

    /// <summary>Libraries that no executable uses.</summary>
    public int UnusedLibraries { get; }

    public string? ComponentOfProject(string projectId) => _componentOfProject.GetValueOrDefault(projectId);

    /// <summary>The component that owns a file: the one of the nearest project above it. Null for a file in no project or in an orchestrator.</summary>
    public string? ComponentOfFile(string path)
    {
        string? best = null;
        var length = -1;
        foreach (var (directory, id) in _directories)
            if ((directory.Length == 0 || path.StartsWith(directory + "/", StringComparison.Ordinal)) && directory.Length > length)
            {
                best = id;
                length = directory.Length;
            }

        return best is null ? null : _componentOfProject.GetValueOrDefault(best);
    }

    public string? ProjectOfFile(string path)
    {
        string? best = null;
        var length = -1;
        foreach (var (directory, id) in _directories)
            if ((directory.Length == 0 || path.StartsWith(directory + "/", StringComparison.Ordinal)) && directory.Length > length)
            {
                best = id;
                length = directory.Length;
            }

        return best;
    }

    public static ComponentModel Build(IReadOnlyList<InventoryItem> allProjects, IReadOnlyList<ParsedFile> files, InventoryOptions options)
    {
        var projects = allProjects.Where(p => !ProjectInventoryEngine.IsTestPath(p.Source.File)).ToList();
        var directories = projects.Select(p => (Dir(p.Source.File), p.Id)).ToList();

        string? ProjectOf(string path) =>
            directories.Where(d => d.Item1.Length == 0 || path.StartsWith(d.Item1 + "/", StringComparison.Ordinal)).OrderByDescending(d => d.Item1.Length).Select(d => d.Item2).FirstOrDefault();

        var orchestrators = files
            .Where(f => f.Root.ToFullString().Contains("DistributedApplication", StringComparison.Ordinal))
            .Select(f => ProjectOf(f.Path))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        var byId = projects.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var references = projects.ToDictionary(
            p => p.Id,
            p => p.Attributes["projectReferenceIds"].Split(", ", StringSplitOptions.RemoveEmptyEntries).Where(byId.ContainsKey).ToList(),
            StringComparer.Ordinal);

        var executables = projects
            .Where(p => p.Attributes["outputType"] == "Exe" && !orchestrators.Contains(p.Id))
            .Select(p => p.Id)
            .Order(StringComparer.Ordinal)
            .ToList();

        Dictionary<string, HashSet<string>> closures = new(StringComparer.Ordinal);
        foreach (var exe in executables)
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            Stack<string> todo = new([exe]);
            while (todo.Count > 0)
                foreach (var reference in references[todo.Pop()])
                    if (seen.Add(reference)) todo.Push(reference);
            closures[exe] = seen;
        }

        var names = Names(projects, executables, byId);
        Dictionary<string, List<string>> libraries = executables.ToDictionary(e => e, _ => new List<string>(), StringComparer.Ordinal);
        List<string> shared = [];
        var unused = 0;
        foreach (var library in projects.Select(p => p.Id).Where(id => !executables.Contains(id) && !orchestrators.Contains(id)).Order(StringComparer.Ordinal))
        {
            var users = executables.Where(e => closures[e].Contains(library)).ToList();
            if (users.Count == 1) libraries[users[0]].Add(library);
            else if (users.Count > 1) shared.Add(library);
            else unused++;
        }

        List<(string Name, string Role, List<string> Executables, List<string> Libraries, List<string> UsedBy, string Sdk)> raw = [];
        foreach (var exe in executables)
        {
            var sdk = byId[exe].Attributes["sdk"];
            raw.Add((names[exe], sdk is "Microsoft.NET.Sdk.Web" or "Microsoft.NET.Sdk.Worker" ? "service" : "application", [exe], libraries[exe], [], sdk));
        }

        foreach (var library in shared)
            raw.Add((names[library], "shared-library", [], [library], [.. executables.Where(e => closures[e].Contains(library)).Select(e => names[e])], byId[library].Attributes["sdk"]));

        // Components the configuration joins under one name.
        foreach (var (target, joined) in options.Merge.OrderBy(m => m.Key, StringComparer.Ordinal))
        {
            var members = raw.Where(r => r.Name == target || joined.Contains(r.Name)).ToList();
            if (members.Count == 0) continue;
            foreach (var m in members) raw.Remove(m);
            raw.Add((target, members[0].Role, [.. members.SelectMany(m => m.Executables).Distinct(StringComparer.Ordinal)], [.. members.SelectMany(m => m.Libraries).Distinct(StringComparer.Ordinal)], [], members[0].Sdk));
        }

        // Users of a shared library are named after the merge too.
        var renamed = options.Merge.SelectMany(m => m.Value.Select(v => (From: v, To: m.Key))).ToDictionary(x => x.From, x => x.To, StringComparer.Ordinal);
        var components = raw
            .Select(r => new ComponentInfo(
                r.Name, r.Role, [.. r.Executables.Order(StringComparer.Ordinal)], [.. r.Libraries.Order(StringComparer.Ordinal)],
                [.. r.UsedBy.Select(u => renamed.GetValueOrDefault(u, u)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], r.Sdk))
            .OrderBy(c => c.Role == "shared-library" ? 1 : 0).ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        Dictionary<string, string> ownerOfProject = new(StringComparer.Ordinal);
        foreach (var c in components)
            foreach (var id in c.Executables.Concat(c.Libraries)) ownerOfProject[id] = c.Name;

        return new ComponentModel(components, ownerOfProject, [.. directories], [.. orchestrators.Order(StringComparer.Ordinal)], unused);
    }

    private static string Dir(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    // The project's name; when several executables or libraries share one, the folders that tell them apart ("Account/Query").
    private static Dictionary<string, string> Names(IReadOnlyList<InventoryItem> projects, List<string> executables, Dictionary<string, InventoryItem> byId)
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach (var group in projects.GroupBy(p => p.Name, StringComparer.Ordinal))
        {
            var members = group.ToList();
            if (members.Count == 1)
            {
                names[members[0].Id] = group.Key;
                continue;
            }

            var paths = members.Select(m => Dir(m.Source.File).Split('/')).ToList();
            foreach (var m in members)
            {
                var mine = Dir(m.Source.File).Split('/');
                var common = 0;
                // Keep at least two folders when there are that many: "Cart/Command", not "Command".
                while (common < mine.Length - 1 && mine.Length - 1 - common > 2 && paths.All(o => o.Length > common && o[common] == mine[common])) common++;
                var tail = mine[common..^1];
                names[m.Id] = tail.Length > 0 && executables.Contains(m.Id) ? string.Join('/', tail) : $"{group.Key} [{Dir(m.Source.File)}]";
            }
        }

        // Two components that still share a name keep their folders.
        var duplicated = names.GroupBy(n => n.Value, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var id in names.Where(n => duplicated.Contains(n.Value)).Select(n => n.Key).ToList())
            names[id] = $"{byId[id].Name} [{Dir(byId[id].Source.File)}]";
        return names;
    }
}
