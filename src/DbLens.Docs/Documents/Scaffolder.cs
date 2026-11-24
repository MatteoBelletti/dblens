using DbLens.Docs.Inventory;

namespace DbLens.Docs.Documents;

public sealed record ScaffoldReport(
    IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Stale, IReadOnlyList<string> MarkedHuman,
    /// <summary>Items whose generated text changed.</summary>
    IReadOnlyList<string> Changed);

public sealed record ScaffoldResult(IReadOnlyDictionary<string, string> Files, ScaffoldReport Report);

/// <summary>
/// Writes the generated regions of the project documents from an inventory, and keeps what a person or a
/// model wrote: the text outside the regions, and the slots, which change state but never text. Pure.
/// </summary>
public static class Scaffolder
{
    /// <param name="existing">The documents already there, by path from the repository root ("docs/project/api.md").</param>
    public static ScaffoldResult Scaffold(ProjectInventory inventory, IReadOnlyDictionary<string, string> existing, DocsOptions options)
    {
        var labels = DocLabels.For(options.Language);
        var plan = DocPlan.For(inventory, options);
        var hashes = inventory.Items.ToDictionary(i => i.Id, ItemHash.Of, StringComparer.Ordinal);
        Dictionary<string, string> files = new(StringComparer.Ordinal);
        List<string> added = [], removed = [], stale = [], human = [], changed = [];

        foreach (var (file, desired) in plan)
        {
            var path = $"{options.Folder}/{file}";
            var parsed = existing.TryGetValue(path, out var text) ? DocumentModel.Parse(text) : null;
            if (parsed is { Problems.Count: > 0 })
                throw new ArgumentException($"{path} is not well formed and was not rewritten: {parsed.Problems[0]}.");

            var duplicate = parsed?.Blocks.GroupBy(b => b.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
            if (duplicate is not null)
                throw new ArgumentException($"{path} holds {duplicate.Key} twice and was not rewritten.");

            if (parsed is null && desired.Count == 0) continue;

            var previous = (parsed?.Blocks ?? []).ToDictionary(b => b.Id, StringComparer.Ordinal);
            List<DocBlock> blocks = [];
            foreach (var d in desired)
            {
                previous.TryGetValue(d.Id, out var old);
                var gen = ItemHash.OfText(d.Generated);
                if (old is null) added.Add(d.Id);
                else if (old.Gen != gen) changed.Add(d.Id);

                DocSlot? slot = d.MaxLines is { } max
                    ? old?.Slot is { } s ? Evolve(s, d.Hash, max, stale, human) : new DocSlot(d.Id, max, "inferred", null, d.Hash, ItemHash.OfText(""), "")
                    : null;
                blocks.Add(new DocBlock(d.Kind, d.Id, d.Hash, gen, d.Generated, slot, old?.Trailing ?? "", 0));
            }

            List<string> kept = [];
            foreach (var gone in previous.Values.Where(b => desired.All(d => d.Id != b.Id)))
            {
                // What a person wrote after a region stays in the file, at its end, so that no text is lost.
                if (gone.Trailing.Length > 0) kept.Add(gone.Trailing);
                removed.Add((gone.Slot is { Text.Length: > 0 } ? $"{gone.Id} (it held text)" : gone.Id) + (gone.Trailing.Length > 0 ? " (its note was kept at the end of the file)" : ""));
            }

            if (kept.Count > 0 && blocks.Count > 0)
                blocks[^1] = blocks[^1] with { Trailing = string.Join("\n\n", [blocks[^1].Trailing, .. kept]).Trim('\n') };

            var (title, intro) = DocPlan.Header(file, labels);
            var leading = parsed is null ? $"# {title}\n\n{intro}" : parsed.Leading;
            if (blocks.Count == 0 && kept.Count > 0) leading = string.Join("\n\n", [leading, .. kept]).Trim('\n');
            files[path] = DocumentModel.Render(new ParsedDocument(leading, blocks, []));
        }

        return new ScaffoldResult(files, new ScaffoldReport(added, removed, stale, human, changed));
    }

    private static DocSlot Evolve(DocSlot slot, string itemHash, int max, List<string> stale, List<string> human)
    {
        var textHash = ItemHash.OfText(slot.Text);

        // Nothing written yet: nothing to protect.
        if (slot.Text.Length == 0)
            return slot with { MaxLines = max, Evidence = "inferred", Was = null, ItemHash = itemHash, TextHash = textHash };

        // A model's words that a person has since changed are the person's, whether or not the item changed too.
        var kind = slot.Evidence == "stale" ? slot.Was ?? "inferred" : slot.Evidence;
        if (kind == "inferred" && textHash != slot.TextHash)
        {
            human.Add(slot.Id);
            kind = "human";
            slot = slot with { TextHash = textHash };
        }

        if (slot.ItemHash != itemHash)
        {
            if (slot.Evidence != "stale") stale.Add(slot.Id);
            return slot with { MaxLines = max, Evidence = "stale", Was = kind };
        }

        return slot with { MaxLines = max, Evidence = kind, Was = null };
    }

    /// <summary>A person confirms a slot as they now read it: it is theirs, and current.</summary>
    public static string Confirm(string documentText, string id, string itemHash)
    {
        var parsed = DocumentModel.Parse(documentText);
        var block = parsed.Blocks.FirstOrDefault(b => b.Id == id && b.Slot is not null)
                    ?? throw new ArgumentException($"No slot for {id} in this document.");
        var slot = block.Slot! with { Evidence = "human", Was = null, ItemHash = itemHash, TextHash = ItemHash.OfText(block.Slot!.Text) };
        return DocumentModel.Render(parsed with { Blocks = [.. parsed.Blocks.Select(b => b.Id == id ? b with { Slot = slot } : b)] });
    }
}
