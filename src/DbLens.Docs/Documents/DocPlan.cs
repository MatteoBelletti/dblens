using System.Text;
using DbLens.Docs.Inventory;

namespace DbLens.Docs.Documents;

/// <summary>Where the documents live and in which language the tool's own words are.</summary>
public sealed record DocsOptions(string Language = "en", string Directory = "docs/project")
{
    public string Folder => Directory.Replace('\\', '/').TrimEnd('/');
}

/// <summary>What a document should hold, derived from the inventory alone: the scaffold writes it and the check compares with it.</summary>
internal sealed record Desired(DocBlockKind Kind, string Id, string Hash, string Generated, int? MaxLines)
{
    public static Desired Text(string id, string generated) => new(DocBlockKind.Generated, id, "", generated, null);
}

internal static class DocPlan
{
    public const string DetailFolder = "components";

    public static readonly string[] Files = ["overview.md", "architecture.md", "data-model.md", "api.md", "configuration.md", "integrations.md"];

    /// <summary>"Ordering.API" as "ordering-api": the name of a component's detail document.</summary>
    public static string Slug(string name)
    {
        StringBuilder slug = new();
        foreach (var c in name.ToLowerInvariant())
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9')) slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        return slug.ToString().Trim('-');
    }

    public static string DetailFile(string componentName) => $"{DetailFolder}/{Slug(componentName)}.md";

    public static bool IsDetailFile(string file) =>
        file.StartsWith(DetailFolder + "/", StringComparison.Ordinal) && file.EndsWith(".md", StringComparison.Ordinal)
        && file.Length > DetailFolder.Length + 4 && file[(DetailFolder.Length + 1)..^3].All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>The documents that exist: the fixed ones, and a detail document for each component someone asked about. Relative to the docs folder.</summary>
    public static IReadOnlyList<string> Present(IEnumerable<string> paths, DocsOptions options)
    {
        var prefix = options.Folder + "/";
        var relative = paths.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p[prefix.Length..]).ToList();
        return [.. Files.Concat(relative.Where(IsDetailFile).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))];
    }

    private static readonly ItemKind[] OverviewKinds = [ItemKind.Component, ItemKind.Event, ItemKind.Link, ItemKind.Flow, ItemKind.Detail];

    private static string FileOf(ItemKind kind) => kind switch
    {
        ItemKind.Project => "architecture.md",
        ItemKind.Context or ItemKind.Entity => "data-model.md",
        ItemKind.Endpoint => "api.md",
        ItemKind.Config => "configuration.md",
        _ => "integrations.md",
    };

    /// <summary>The words an item's slot may hold, in lines. Null: the item gets its place but no words (a configuration key).</summary>
    public static int? MaxLines(ItemKind kind) => kind switch
    {
        ItemKind.Context => 4,
        ItemKind.Config => null,
        ItemKind.Flow => 3,
        ItemKind.Detail => DetailLines,
        _ => 2,
    };

    /// <summary>A detail document's narrative is a page, not a caption.</summary>
    public const int DetailLines = 80;

    public static (string Title, string Intro) Header(string file, DocLabels l, ProjectInventory inventory)
    {
        if (IsDetailFile(file))
        {
            var name = inventory.Items.FirstOrDefault(i => i.Kind == ItemKind.Detail && Slug(i.Name) == file[(DetailFolder.Length + 1)..^3])?.Name ?? file;
            return (name, l["detailIntro"]);
        }

        return file switch
        {
            "overview.md" => (l["overview"], l["overviewIntro"]),
            "architecture.md" => (l["architecture"], l["architectureIntro"]),
            "data-model.md" => (l["dataModel"], l["dataModelIntro"]),
            "api.md" => (l["api"], l["apiIntro"]),
            "configuration.md" => (l["configuration"], l["configurationIntro"]),
            _ => (l["integrations"], l["integrationsIntro"]),
        };
    }

    /// <param name="files">The documents that exist or are being started, relative to the docs folder: the detail documents among them are planned.</param>
    public static IReadOnlyDictionary<string, List<Desired>> For(ProjectInventory inventory, DocsOptions options, IEnumerable<string>? files = null)
    {
        var l = DocLabels.For(options.Language);
        var byFile = Files.ToDictionary(f => f, _ => new List<Desired>(), StringComparer.Ordinal);

        if (inventory.Items.Any(i => i.Kind == ItemKind.Project))
            byFile["architecture.md"].Add(Desired.Text(ProjectGraph.Id, ProjectGraph.Mermaid(inventory)));

        foreach (var item in inventory.Items.Where(i => !OverviewKinds.Contains(i.Kind)).OrderBy(i => SortKey(i), StringComparer.Ordinal))
            byFile[FileOf(item.Kind)].Add(new Desired(DocBlockKind.Item, item.Id, ItemHash.Of(item), FactsRenderer.Render(item, l), MaxLines(item.Kind)));

        Overview(inventory, l, byFile["overview.md"]);

        foreach (var file in (files ?? []).Where(IsDetailFile).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var owners = inventory.Items.Where(i => i.Kind == ItemKind.Detail && DetailFile(i.Name) == file).ToList();
            if (owners.Count == 0)
            {
                byFile[file] = [];
                continue;
            }

            var detail = owners.OrderBy(o => o.Name, StringComparer.Ordinal).First();

            var facts = OverviewRenderer.Detail(inventory, detail, l);
            byFile[file] =
            [
                Desired.Text($"detail-facts:{detail.Name}", facts),
                new Desired(DocBlockKind.Item, detail.Id, ItemHash.OfText($"{ItemHash.Of(detail)}|{facts}"), $"### {l["narrative"]}", DetailLines),
            ];
        }

        return byFile;
    }

    private static void Overview(ProjectInventory inventory, DocLabels l, List<Desired> blocks)
    {
        var components = inventory.Items.Where(OverviewRenderer.IsComponent).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        if (components.Count == 0) return;

        blocks.Add(Desired.Text(OverviewRenderer.GraphId, OverviewRenderer.Graph(inventory, l)));
        foreach (var c in components)
            blocks.Add(new Desired(DocBlockKind.Item, c.Id, ItemHash.Of(c), OverviewRenderer.ComponentFacts(c, l), MaxLines(c.Kind)));

        var flows = inventory.Items.Where(i => i.Kind == ItemKind.Flow).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
        if (flows.Count > 0)
        {
            blocks.Add(Desired.Text(OverviewRenderer.FlowsId, $"## {l["flows"]}\n\n{l["flowsIntro"]}"));
            foreach (var f in flows)
                blocks.Add(new Desired(DocBlockKind.Item, f.Id, ItemHash.Of(f), OverviewRenderer.Flow(f, l), MaxLines(f.Kind)));
        }

        if (inventory.Items.Any(i => i.Kind == ItemKind.Event))
            blocks.Add(Desired.Text(OverviewRenderer.EventsId, OverviewRenderer.Events(inventory, l)));
        if (inventory.Items.Any(i => i.Kind == ItemKind.Component && i.Attributes["role"] == "shared-library"))
            blocks.Add(Desired.Text(OverviewRenderer.SharedId, OverviewRenderer.Shared(inventory, l)));
    }

    // Contexts before the entities of their own context; endpoints by route, then verb; the rest by id.
    private static string SortKey(InventoryItem item) => item.Kind switch
    {
        ItemKind.Context => $"{item.Name}\u0001a",
        ItemKind.Entity => $"{item.Attributes["context"]}\u0001b{item.Id}",
        ItemKind.Endpoint => $"{(item.Attributes["route"].Length > 0 ? item.Attributes["route"] : "￿" + item.Source.File)}\u0001{item.Attributes["verb"]}\u0001{item.Id}",
        _ => $"{(int)item.Kind}\u0001{item.Id}",
    };
}
