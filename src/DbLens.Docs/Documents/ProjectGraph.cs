using System.Text;
using DbLens.Docs.Inventory;

namespace DbLens.Docs.Documents;

/// <summary>The projects and their references as a Mermaid graph: the same inventory always gives the same text.</summary>
public static class ProjectGraph
{
    public const string Id = "graph:projects";

    public static string Mermaid(ProjectInventory inventory)
    {
        var projects = inventory.Items.Where(i => i.Kind == ItemKind.Project).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
        // Two projects of one name share a node: a reference is by name only.
        var node = projects.Select((p, index) => (p, Key: $"p{index}")).GroupBy(x => x.p.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);

        StringBuilder text = new();
        text.Append("```mermaid\ngraph LR\n");
        foreach (var name in node.Keys.Order(StringComparer.Ordinal)) text.Append($"  {node[name]}[\"{name.Replace("\"", "'", StringComparison.Ordinal)}\"]\n");
        foreach (var project in projects)
            foreach (var reference in project.Attributes["projectReferences"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal))
                if (node.TryGetValue(reference, out var target) && node.TryGetValue(project.Name, out var source))
                    text.Append($"  {source} --> {target}\n");
        text.Append("```");
        return text.ToString();
    }
}
