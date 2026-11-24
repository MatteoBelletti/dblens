using System.Text;
using DbLens.Docs.Inventory;

namespace DbLens.Docs.Documents;

/// <summary>
/// The generated text of the overview and of a component's detail: the component graph, a table per component, a sequence
/// diagram per flow, the events and the shared libraries. Pure over the inventory; no model writes any of it.
/// </summary>
internal static class OverviewRenderer
{
    public const string GraphId = "graph:components";
    public const string FlowsId = "section:flows";
    public const string EventsId = "table:events";
    public const string SharedId = "table:shared";

    public static bool IsComponent(InventoryItem item) => item.Kind == ItemKind.Component && item.Attributes["role"] != "shared-library";

    private static string Quote(string text) => text.Replace("\"", "'", StringComparison.Ordinal);

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Row(params string[] cells) => $"| {string.Join(" | ", cells.Select(Cell))} |";

    private static string Table(string[] header, IEnumerable<string[]> rows) =>
        string.Join('\n', new[] { Row(header), Row([.. header.Select(_ => "---")]) }.Concat(rows.Select(r => Row(r))));

    private static string[] Names(string list) => list.Split(", ", StringSplitOptions.RemoveEmptyEntries);

    public static string Graph(ProjectInventory inventory, DocLabels l)
    {
        var components = inventory.Items.Where(IsComponent).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        var node = components.Select((c, i) => (c.Name, Key: $"c{i}")).ToDictionary(x => x.Name, x => x.Key, StringComparer.Ordinal);
        var links = inventory.Items.Where(i => i.Kind == ItemKind.Link && node.ContainsKey(i.Attributes["from"]) && node.ContainsKey(i.Attributes["to"])).ToList();

        StringBuilder text = new();
        text.Append("## ").Append(l["components"]).Append("\n\n```mermaid\ngraph LR\n");
        foreach (var c in components) text.Append($"  {node[c.Name]}[\"{Quote(c.Name)}\"]\n");
        foreach (var pair in links.GroupBy(i => (From: i.Attributes["from"], To: i.Attributes["to"])).OrderBy(g => g.Key.From, StringComparer.Ordinal).ThenBy(g => g.Key.To, StringComparer.Ordinal))
        {
            var kinds = pair.GroupBy(i => i.Attributes["kind"]).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var (from, to) = (node[pair.Key.From], node[pair.Key.To]);
            if (kinds.TryGetValue("event", out var events)) text.Append($"  {from} -->|\"{events} {l["eventsWord"]}\"| {to}\n");
            if (kinds.ContainsKey("call")) text.Append($"  {from} -->|\"{l["callWord"]}\"| {to}\n");
            if (kinds.ContainsKey("data")) text.Append($"  {from} -.->|\"{l["dataWord"]}\"| {to}\n");
            if (!kinds.ContainsKey("event") && !kinds.ContainsKey("call") && !kinds.ContainsKey("data")) text.Append($"  {from} -.->|\"{l["dependsWord"]}\"| {to}\n");
        }

        return text.Append("```").ToString();
    }

    public static string ComponentFacts(InventoryItem c, DocLabels l)
    {
        var a = c.Attributes;
        return $"### {Cell(c.Name)}\n\n" + Table(
            [l["role"], l["infrastructure"], l["privateLibraries"], l["file"]],
            [[l[a["role"]], a["infrastructure"].Length > 0 ? a["infrastructure"] : l["none"], Names(a["libraries"]).Length.ToString(System.Globalization.CultureInfo.InvariantCulture), c.Source.File]]);
    }

    public static string Flow(InventoryItem flow, DocLabels l)
    {
        var a = flow.Attributes;
        var hops = a["hops"].Split(" | ", StringSplitOptions.RemoveEmptyEntries).Select(h => h.Split(" -> ", 2)).Select(p => (From: p[0], Target: p[1].Split(" : ", 2)[0], Event: p[1].Split(" : ", 2)[1])).ToList();
        List<string> order = [a["component"]];
        foreach (var (from, target, _) in hops)
            foreach (var name in new[] { from, target }.Where(n => n != "(no consumer)" && !order.Contains(n))) order.Add(name);
        var key = order.Select((n, i) => (n, k: $"c{i}")).ToDictionary(x => x.n, x => x.k, StringComparer.Ordinal);
        var isEndpoint = a["entry"].StartsWith("endpoint:", StringComparison.Ordinal);

        StringBuilder text = new();
        text.Append($"### {Cell(flow.Name)}\n\n```mermaid\nsequenceDiagram\n");
        if (isEndpoint) text.Append($"  participant caller as \"{Quote(l["caller"])}\"\n");
        foreach (var n in order) text.Append($"  participant {key[n]} as \"{Quote(n)}\"\n");
        text.Append(isEndpoint ? $"  caller->>{key[a["component"]]}: {flow.Name}\n" : $"  Note over {key[a["component"]]}: {flow.Name} ({l["background"]})\n");
        foreach (var (from, target, eventName) in hops)
        {
            text.Append(target == "(no consumer)" ? $"  Note over {key[from]}: {eventName} ({l["noConsumer"]})\n" : $"  {key[from]}->>{key[target]}: {eventName}\n");
        }

        return text.Append("```\n\n").Append(l["involved"]).Append(": ").Append(a["components"]).ToString();
    }

    public static string Events(ProjectInventory inventory, DocLabels l)
    {
        var events = inventory.Items.Where(i => i.Kind == ItemKind.Event).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
        return $"## {l["eventsTitle"]}\n\n" + Table(
            [l["event"], l["producers"], l["consumers"]],
            events.Select(e => new[] { e.Name, OrNone(e.Attributes["producers"], l), OrNone(e.Attributes["consumers"], l) }));
    }

    public static string Shared(ProjectInventory inventory, DocLabels l) =>
        $"## {l["sharedTitle"]}\n\n" + Table(
            [l["library"], l["sharedUsedBy"]],
            inventory.Items.Where(i => i.Kind == ItemKind.Component && i.Attributes["role"] == "shared-library").OrderBy(i => i.Name, StringComparer.Ordinal).Select(i => new[] { i.Name, OrNone(i.Attributes["usedBy"], l) }));

    private static string OrNone(string value, DocLabels l) => value.Length > 0 ? value : l["none"];

    /// <summary>The facts of one component, from the whole inventory: what it exposes, publishes, consumes, reads and takes part in.</summary>
    public static string Detail(ProjectInventory inventory, InventoryItem detail, DocLabels l)
    {
        var name = detail.Name;
        var component = inventory.Items.First(i => i.Id == $"component:{name}");
        var projectIds = Names(component.Attributes["executables"]).Concat(Names(component.Attributes["libraries"])).ToHashSet(StringComparer.Ordinal);
        var directories = inventory.Items.Where(i => i.Kind == ItemKind.Project && projectIds.Contains(i.Id)).Select(i => i.Source.File.Contains('/') ? i.Source.File[..i.Source.File.LastIndexOf('/')] : "").ToList();
        bool Mine(InventoryItem i) => directories.Any(d => d.Length == 0 || i.Source.File.StartsWith(d + "/", StringComparison.Ordinal));

        StringBuilder text = new();
        text.Append(ComponentFacts(component, l));

        void Section(string title, string[] header, IEnumerable<string[]> rows)
        {
            var list = rows.ToList();
            if (list.Count > 0) text.Append($"\n\n### {title}\n\n").Append(Table(header, list));
        }

        Section(l["endpointsTitle"], [l["verb"], l["route"], l["handler"], l["file"]],
            inventory.Items.Where(i => i.Kind == ItemKind.Endpoint && Mine(i)).OrderBy(i => i.Attributes["route"], StringComparer.Ordinal).ThenBy(i => i.Attributes["verb"], StringComparer.Ordinal)
                .Select(i => new[] { i.Attributes["verb"], i.Attributes["route"].Length > 0 ? i.Attributes["route"] : l["unresolved"], OrNone(i.Attributes["handler"], l), i.Source.File }));
        Section(l["publishesTitle"], [l["event"], l["consumers"]],
            inventory.Items.Where(i => i.Kind == ItemKind.Event && Names(i.Attributes["producers"]).Contains(name)).OrderBy(i => i.Name, StringComparer.Ordinal)
                .Select(i => new[] { i.Name, OrNone(i.Attributes["consumers"], l) }));
        Section(l["consumesTitle"], [l["event"], l["producers"], l["handler"]],
            inventory.Items.Where(i => i.Kind == ItemKind.Event && Names(i.Attributes["consumers"]).Contains(name)).OrderBy(i => i.Name, StringComparer.Ordinal)
                .Select(i => new[] { i.Name, OrNone(i.Attributes["producers"], l), string.Join(", ", Names(i.Attributes["handlers"]).Where(h => h.StartsWith(name + ".", StringComparison.Ordinal)).Select(h => h[(name.Length + 1)..])) }));
        Section(l["backgroundTitle"], [l["class"], l["file"]],
            inventory.Items.Where(i => i.Kind == ItemKind.HostedService && Mine(i)).OrderBy(i => i.Name, StringComparer.Ordinal).Select(i => new[] { i.Name, i.Source.File }));
        Section(l["dataTitle"], [l["context"], l["entities"], l["table"]],
            inventory.Items.Where(i => i.Kind == ItemKind.Context && Mine(i)).OrderBy(i => i.Id, StringComparer.Ordinal)
                .Select(i => new[] { i.Name, i.Attributes["entities"], i.Attributes["defaultSchema"].Length > 0 ? i.Attributes["defaultSchema"] : l["none"] }));
        Section(l["connectionsTitle"], [l["from"], l["to"], l["kindWord"], l["via"]],
            inventory.Items.Where(i => i.Kind == ItemKind.Link && i.Attributes["kind"] != "event" && (i.Attributes["from"] == name || i.Attributes["to"] == name)).OrderBy(i => i.Id, StringComparer.Ordinal)
                .Select(i => new[] { i.Attributes["from"], i.Attributes["to"], i.Attributes["kind"], i.Attributes["via"] }));
        Section(l["relatedFlows"], [l["flowWord"], l["involved"]],
            inventory.Items.Where(i => i.Kind == ItemKind.Flow && Names(i.Attributes["components"]).Contains(name)).OrderBy(i => i.Id, StringComparer.Ordinal)
                .Select(i => new[] { i.Name, i.Attributes["components"] }));
        return text.ToString();
    }
}
