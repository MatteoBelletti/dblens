using System.ComponentModel;
using DbLens.Docs.Documents;
using DbLens.Docs.Inventory;
using ModelContextProtocol.Server;

namespace DbLens.Docs.Mcp;

public sealed record InventoryView(IReadOnlyList<InventoryItem> Items, IReadOnlyList<CoverageNote> Coverage, int Total);

public sealed record SlotList(int Total, int Returned, IReadOnlyList<SlotInfo> Slots, string Note);

public sealed record SlotWritten(string Id, string File, string Evidence, int Lines);

[McpServerToolType]
public sealed class DocsTools(DocsWorkspace workspace)
{
    private const int MaxItems = 200;

    [McpServerTool(ReadOnly = true), Description(
        "The inventory of the .NET solution this server was started on, read from syntax: projects, DbContexts, entities, endpoints, configuration keys, HTTP clients, background services, each with its source file and line. " +
        "IMPORTANT: nothing is compiled or run, so a route built at run time, a message handler or a convention-based controller is named in coverage, not guessed. Configuration values are never read.")]
    public InventoryView GetProjectInventory(
        [Description("Only items of this kind: Project, Context, Entity, Endpoint, Config, HttpClient or HostedService. Omit for all.")]
        string? kind = null,
        [Description("Only items whose id starts with this text, e.g. 'endpoint:GET /orders'.")]
        string? idPrefix = null)
    {
        var inventory = workspace.Inventory();
        ItemKind? wanted = null;
        if (kind is { Length: > 0 })
            wanted = Enum.TryParse<ItemKind>(kind, ignoreCase: true, out var parsed) ? parsed : throw new ArgumentException($"Unknown kind {kind}.");

        var items = inventory.Items
            .Where(i => (wanted is null || i.Kind == wanted) && (idPrefix is not { Length: > 0 } || i.Id.StartsWith(idPrefix, StringComparison.Ordinal)))
            .ToList();
        return new InventoryView([.. items.Take(MaxItems)], inventory.Coverage, items.Count);
    }

    [McpServerTool(ReadOnly = true), Description(
        "The slots of the project documents where words may be written, each with the facts of the item it is about and who wrote what is there. state: empty, stale (the item changed since) or current. " +
        "IMPORTANT: a slot with protected=true holds a person's words and must not be replaced. Run dblens-docs scaffold first if a document or a slot is missing.")]
    public SlotList GetDocSlots(
        [Description("empty, stale, current or all. Default: empty and stale, the slots that need words.")]
        string state = "needed",
        [Description("Only slots whose id starts with this text.")]
        string? idPrefix = null)
    {
        if (state is not ("needed" or "empty" or "stale" or "current" or "all")) throw new ArgumentException("state is one of needed, empty, stale, current, all.");

        var all = SlotWriter.List(workspace.Inventory(), workspace.Documents(), workspace.Options)
            .Where(s => idPrefix is not { Length: > 0 } || s.Id.StartsWith(idPrefix, StringComparison.Ordinal))
            .ToList();
        var chosen = all.Where(s => state switch { "all" => true, "needed" => s.State != "current", _ => s.State == state }).ToList();
        var note = all.Count == 0 ? "No slots: the documents do not exist yet (run dblens-docs scaffold) or nothing matches." : "";
        return new SlotList(chosen.Count, Math.Min(chosen.Count, MaxItems), [.. chosen.Take(MaxItems)], note);
    }

    [McpServerTool(ReadOnly = true), Description(
        "Compares the project documents with the code, as dblens-docs check does: missing items, items gone from the code, items changed, generated text edited by hand, slots too long, empty or stale. " +
        "IMPORTANT: it fixes nothing; a missing or outdated item is fixed by running dblens-docs scaffold.")]
    public DocCheckResult CheckDocCoverage(
        [Description("Stale slots are errors.")]
        bool strict = false,
        [Description("Empty slots are errors.")]
        bool requireSlots = false) =>
        DocumentCheck.Check(workspace.Inventory(), workspace.Documents(), workspace.Options, strict, requireSlots);

    [McpServerTool(Destructive = false, Idempotent = true), Description(
        "Writes the words of one slot of the project documents (one purpose, at most the slot's max lines, plain text, no blank lines). The only thing this server writes. " +
        "IMPORTANT: it refuses a slot that holds a person's words, a document whose item changed since it was generated, text with an HTML comment, and any id without a slot. The slot is marked inferred: a person confirms it by editing it.")]
    public SlotWritten SetDocSlot(
        [Description("The item id, as get_doc_slots lists it, e.g. 'endpoint:GET /orders/{id}'.")]
        string id,
        [Description("The words: what the item is for, in at most the slot's max lines.")]
        string text)
    {
        var (path, document) = SlotWriter.Set(workspace.Inventory(), workspace.Documents(), workspace.Options, id, text);
        workspace.Write(path, document);
        return new SlotWritten(id, path, "inferred", text.Trim().Split('\n').Length);
    }
}
