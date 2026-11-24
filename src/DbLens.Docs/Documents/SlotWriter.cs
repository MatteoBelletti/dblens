using DbLens.Docs.Inventory;

namespace DbLens.Docs.Documents;

/// <param name="State"><c>empty</c>, <c>stale</c> (the item changed since the words were written) or <c>current</c>.</param>
/// <param name="Protected">True when the words are a person's (or were, before the item changed): a model must not replace them.</param>
public sealed record SlotInfo(
    string Id, string Kind, string Name, string File, string Source, int MaxLines, string State, string Evidence, bool Protected,
    string Text, IReadOnlyDictionary<string, string> Facts);

/// <summary>What the slots of the documents hold and who may change them. Pure.</summary>
public static class SlotWriter
{
    public const int MaxLineLength = 300;
    private const int NarrativeLineLength = 800;

    // A narrative slot (a page) may have paragraphs; a caption may not have blank lines.
    private const int NarrativeLines = 10;

    /// <summary>The slots, with the item each one is about, in document order.</summary>
    public static IReadOnlyList<SlotInfo> List(ProjectInventory inventory, IReadOnlyDictionary<string, string> documents, DocsOptions options) =>
        List(inventory, documents, options, Wanted(inventory, options, DocPlan.Present(documents.Keys, options)));

    private static IReadOnlyList<SlotInfo> List(
        ProjectInventory inventory, IReadOnlyDictionary<string, string> documents, DocsOptions options, Dictionary<string, Desired> wanted)
    {
        var present = DocPlan.Present(documents.Keys, options);
        var items = inventory.Items.GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        List<SlotInfo> slots = [];
        foreach (var file in present)
        {
            var path = $"{options.Folder}/{file}";
            if (!documents.TryGetValue(path, out var text)) continue;

            foreach (var block in DocumentModel.Parse(text).Blocks.Where(b => b.Slot is not null && wanted.ContainsKey(b.Id) && items.ContainsKey(b.Id)))
            {
                var (slot, item, desired) = (block.Slot!, items[block.Id], wanted[block.Id]);
                var state = slot.Text.Length == 0 ? "empty" : slot.Evidence == "stale" || slot.ItemHash != desired.Hash ? "stale" : "current";
                slots.Add(new SlotInfo(
                    slot.Id, item.Kind.ToString(), item.Name, path, $"{item.Source.File}:{item.Source.Line}", slot.MaxLines, state,
                    slot.Evidence, IsPersons(slot), slot.Text, ItemHash.Shown(item).ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal)));
            }
        }

        return slots;
    }

    private static Dictionary<string, Desired> Wanted(ProjectInventory inventory, DocsOptions options, IReadOnlyList<string> present) =>
        DocPlan.For(inventory, options, present)
            .SelectMany(p => p.Value).Where(d => d.Kind == DocBlockKind.Item)
            .GroupBy(d => d.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>The hash a slot must carry to be current: the one the documents are planned with (a detail slot's is not the item's own).</summary>
    public static string CurrentHash(ProjectInventory inventory, IReadOnlyDictionary<string, string> documents, DocsOptions options, string id) =>
        Wanted(inventory, options, DocPlan.Present(documents.Keys, options)).GetValueOrDefault(id)?.Hash
        ?? throw new ArgumentException($"{id} has no slot in the documents.");

    /// <summary>
    /// A model writes the words of one slot. It may not replace a person's words, write more than the slot
    /// allows, or write anything that looks like a region marker; the item's facts must be current, so the
    /// words are about what the code says now. Returns the document path and its new text.
    /// </summary>
    public static (string Path, string Text) Set(
        ProjectInventory inventory, IReadOnlyDictionary<string, string> documents, DocsOptions options, string id, string words)
    {
        if (!inventory.Items.Any(i => i.Id == id)) throw new ArgumentException($"{id} is not in the inventory.");
        var wanted = Wanted(inventory, options, DocPlan.Present(documents.Keys, options));
        var path = List(inventory, documents, options, wanted).FirstOrDefault(s => s.Id == id)?.File
                   ?? throw new ArgumentException($"{id} has no slot in the documents: it may not need one, or dblens-docs scaffold has not run.");
        var parsed = DocumentModel.Parse(documents[path]);
        if (parsed.Problems.Count > 0) throw new ArgumentException($"{path} is not well formed: {parsed.Problems[0]}.");

        var block = parsed.Blocks.First(b => b.Id == id);
        var slot = block.Slot!;
        var hash = wanted[id].Hash;
        if (block.Hash != hash) throw new ArgumentException($"{id} changed since the document was generated: run dblens-docs scaffold first.");
        if (IsPersons(slot))
            throw new ArgumentException($"The slot of {id} holds words written by a person; a model does not replace them. Ask the person to edit or delete it.");

        var text = Validate(words, slot.MaxLines);
        var written = slot with { Evidence = "inferred", Was = null, ItemHash = hash, TextHash = ItemHash.OfText(text), Text = text };
        return (path, DocumentModel.Render(parsed with { Blocks = [.. parsed.Blocks.Select(b => b.Id == id ? b with { Slot = written } : b)] }));
    }

    /// <summary>
    /// Words that are, or may now be, a person's: marked human, or a model's words whose text is no longer
    /// what the tool wrote (a person edited them and no scaffold has noticed yet).
    /// </summary>
    public static bool IsPersons(DocSlot slot) =>
        slot.Text.Length > 0 && (slot.Evidence == "human" || slot is { Evidence: "stale", Was: "human" } || ItemHash.OfText(slot.Text) != slot.TextHash);

    internal static string Validate(string words, int maxLines)
    {
        var narrative = maxLines >= NarrativeLines;
        var text = words.Replace("\r\n", "\n").Trim('\n', ' ', '\t');
        if (text.Length == 0) throw new ArgumentException("The words are empty.");
        if (text.Contains("<!--", StringComparison.Ordinal) || text.Contains("-->", StringComparison.Ordinal))
            throw new ArgumentException("The words contain '<!--' or '-->' (also inside a diagram arrow such as -->>): the regions are marked with HTML comments.");

        var lines = text.Split('\n');
        var longest = narrative ? NarrativeLineLength : MaxLineLength;
        if (lines.Any(l => l.Length > longest)) throw new ArgumentException($"A line is longer than {longest} characters.");
        if (!narrative && lines.Any(l => l.Trim().Length == 0)) throw new ArgumentException("Blank lines are not allowed: write the slot as consecutive lines.");
        if (lines.Length > maxLines) throw new ArgumentException($"{lines.Length} lines; this slot takes at most {maxLines}.");
        return text;
    }
}
