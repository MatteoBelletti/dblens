using DbLens.Docs.Inventory;

namespace DbLens.Docs.Documents;

/// <summary>Where the documents live and in which language the tool's own words are.</summary>
public sealed record DocsOptions(string Language = "en", string Directory = "docs/project")
{
    public string Folder => Directory.Replace('\\', '/').TrimEnd('/');
}

/// <summary>What a document should hold, derived from the inventory alone: the scaffold writes it and the check compares with it.</summary>
internal sealed record Desired(DocBlockKind Kind, string Id, string Hash, string Generated, int? MaxLines);

internal static class DocPlan
{
    public static readonly string[] Files = ["architecture.md", "data-model.md", "api.md", "configuration.md", "integrations.md"];

    public static string FileOf(ItemKind kind) => kind switch
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
        _ => 2,
    };

    public static (string Title, string Intro) Header(string file, DocLabels l) => file switch
    {
        "architecture.md" => (l["architecture"], l["architectureIntro"]),
        "data-model.md" => (l["dataModel"], l["dataModelIntro"]),
        "api.md" => (l["api"], l["apiIntro"]),
        "configuration.md" => (l["configuration"], l["configurationIntro"]),
        _ => (l["integrations"], l["integrationsIntro"]),
    };

    public static IReadOnlyDictionary<string, List<Desired>> For(ProjectInventory inventory, DocsOptions options)
    {
        var l = DocLabels.For(options.Language);
        var byFile = Files.ToDictionary(f => f, _ => new List<Desired>(), StringComparer.Ordinal);

        if (inventory.Items.Any(i => i.Kind == ItemKind.Project))
            byFile["architecture.md"].Add(new Desired(DocBlockKind.Generated, ProjectGraph.Id, "", ProjectGraph.Mermaid(inventory), null));

        foreach (var item in inventory.Items.OrderBy(i => SortKey(i), StringComparer.Ordinal))
            byFile[FileOf(item.Kind)].Add(new Desired(DocBlockKind.Item, item.Id, ItemHash.Of(item), FactsRenderer.Render(item, l), MaxLines(item.Kind)));

        return byFile;
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
