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

    /// <summary>The slots, with the item each one is about, in document order.</summary>
    public static IReadOnlyList<SlotInfo> List(ProjectInventory inventory, IReadOnlyDictionary<string, string> documents, DocsOptions options)
    {
        var items = inventory.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        List<SlotInfo> slots = [];
        foreach (var file in DocPlan.Files)
        {
            var path = $"{options.Folder}/{file}";
            if (!documents.TryGetValue(path, out var text)) continue;

            foreach (var block in DocumentModel.Parse(text).Blocks.Where(b => b.Slot is not null && items.ContainsKey(b.Id)))
            {
                var slot = block.Slot!;
                var item = items[block.Id];
                var state = slot.Text.Length == 0 ? "empty" : slot.Evidence == "stale" || slot.ItemHash != ItemHash.Of(item) ? "stale" : "current";
                var isProtected = slot.Text.Length > 0 && (slot.Evidence == "human" || slot is { Evidence: "stale", Was: "human" });
                slots.Add(new SlotInfo(
                    slot.Id, item.Kind.ToString(), item.Name, path, $"{item.Source.File}:{item.Source.Line}", slot.MaxLines, state,
                    slot.Evidence, isProtected, slot.Text, item.Attributes));
            }
        }

        return slots;
    }

    /// <summary>
    /// A model writes the words of one slot. It may not replace a person's words, write more than the slot
    /// allows, or write anything that looks like a region marker; the item's facts must be current, so the
    /// words are about what the code says now. Returns the document path and its new text.
    /// </summary>
    public static (string Path, string Text) Set(
        ProjectInventory inventory, IReadOnlyDictionary<string, string> documents, DocsOptions options, string id, string words)
    {
        var item = inventory.Items.FirstOrDefault(i => i.Id == id) ?? throw new ArgumentException($"{id} is not in the inventory.");
        var path = List(inventory, documents, options).FirstOrDefault(s => s.Id == id)?.File
                   ?? throw new ArgumentException($"{id} has no slot in the documents: it may not need one, or dblens-docs scaffold has not run.");
        var parsed = DocumentModel.Parse(documents[path]);
        if (parsed.Problems.Count > 0) throw new ArgumentException($"{path} is not well formed: {parsed.Problems[0]}.");

        var block = parsed.Blocks.First(b => b.Id == id);
        var slot = block.Slot!;
        var hash = ItemHash.Of(item);
        if (block.Hash != hash) throw new ArgumentException($"{id} changed since the document was generated: run dblens-docs scaffold first.");
        if (slot.Text.Length > 0 && (slot.Evidence == "human" || slot is { Evidence: "stale", Was: "human" }))
            throw new ArgumentException($"The slot of {id} holds words written by a person; a model does not replace them. Ask the person to edit or delete it.");

        var text = Validate(words, slot.MaxLines);
        var written = slot with { Evidence = "inferred", Was = null, ItemHash = hash, TextHash = ItemHash.OfText(text), Text = text };
        return (path, DocumentModel.Render(parsed with { Blocks = [.. parsed.Blocks.Select(b => b.Id == id ? b with { Slot = written } : b)] }));
    }

    internal static string Validate(string words, int maxLines)
    {
        var text = words.Replace("\r\n", "\n").Trim('\n', ' ', '\t');
        if (text.Length == 0) throw new ArgumentException("The words are empty.");
        if (text.Contains("<!--", StringComparison.Ordinal) || text.Contains("-->", StringComparison.Ordinal))
            throw new ArgumentException("The words contain an HTML comment, which is how the regions are marked.");

        var lines = text.Split('\n');
        if (lines.Any(l => l.Length > MaxLineLength)) throw new ArgumentException($"A line is longer than {MaxLineLength} characters.");
        if (lines.Any(l => l.Trim().Length == 0)) throw new ArgumentException("Blank lines are not allowed: write the slot as consecutive lines.");
        if (lines.Length > maxLines) throw new ArgumentException($"{lines.Length} lines; this slot takes at most {maxLines}.");
        return text;
    }
}
