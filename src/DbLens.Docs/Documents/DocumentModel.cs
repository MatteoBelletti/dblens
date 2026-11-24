using System.Text;
using System.Text.RegularExpressions;

namespace DbLens.Docs.Documents;

public enum DocBlockKind
{
    /// <summary>An inventory item: its facts, and usually a slot for words.</summary>
    Item,

    /// <summary>A generated region that belongs to the document, not to an item (the project graph).</summary>
    Generated,
}

/// <summary>The words of one slot, and what is known about who wrote them and about what.</summary>
/// <param name="Evidence"><c>inferred</c> (a model wrote it), <c>human</c> (a person did) or <c>stale</c> (the item changed since).</param>
/// <param name="Was">For a stale slot: what it was before, <c>inferred</c> or <c>human</c>.</param>
/// <param name="ItemHash">The hash of the item when the text was written or confirmed.</param>
/// <param name="TextHash">The hash of the text as the tool wrote it: a different text now means a person edited it.</param>
public sealed record DocSlot(string Id, int MaxLines, string Evidence, string? Was, string ItemHash, string TextHash, string Text);

/// <param name="Hash">The item's hash when the region was generated; empty for a <see cref="DocBlockKind.Generated"/> block.</param>
/// <param name="Gen">The hash of the generated text: a different text now means someone edited it by hand.</param>
/// <param name="Trailing">Text a person wrote after the block, kept with it.</param>
public sealed record DocBlock(DocBlockKind Kind, string Id, string Hash, string Gen, string Generated, DocSlot? Slot, string Trailing, int Line);

public sealed record ParsedDocument(string Leading, IReadOnlyList<DocBlock> Blocks, IReadOnlyList<string> Problems);

/// <summary>
/// A generated Markdown file: regions marked by HTML comments, invisible when it is rendered, and the text
/// a person wrote around them. Parse and render are inverses for a document the tool wrote.
/// </summary>
public static partial class DocumentModel
{
    public const string End = "<!-- dblens:end -->";

    public static string ItemMarker(string id, string hash, string gen) => $"<!-- dblens:item {Escape(id)} hash={hash} gen={gen} -->";

    public static string GeneratedMarker(string id, string gen) => $"<!-- dblens:generated {Escape(id)} gen={gen} -->";

    public static string SlotMarker(DocSlot slot) =>
        $"<!-- dblens:slot {Escape(slot.Id)} purpose max-lines={slot.MaxLines} evidence={slot.Evidence}"
        + (slot.Was is null ? "" : $" was={slot.Was}") + $" hash={slot.ItemHash} text={slot.TextHash} -->";

    // An id may hold "--" or ">", which an HTML comment cannot; "%" is escaped first so that the two are inverses.
    private static string Escape(string id) =>
        id.Replace("%", "%25", StringComparison.Ordinal).Replace(">", "%3E", StringComparison.Ordinal).Replace("--", "-%2D", StringComparison.Ordinal);

    private static string Unescape(string id) =>
        id.Replace("-%2D", "--", StringComparison.Ordinal).Replace("%3E", ">", StringComparison.Ordinal).Replace("%25", "%", StringComparison.Ordinal);

    [GeneratedRegex(@"^<!-- dblens:item (?<id>.+) hash=(?<hash>[0-9a-f]+) gen=(?<gen>[0-9a-f]+) -->$")]
    private static partial Regex ItemRx();

    [GeneratedRegex(@"^<!-- dblens:generated (?<id>.+) gen=(?<gen>[0-9a-f]+) -->$")]
    private static partial Regex GeneratedRx();

    [GeneratedRegex(@"^<!-- dblens:slot (?<id>.+) purpose max-lines=(?<max>\d+) evidence=(?<ev>[a-z]+)(?: was=(?<was>[a-z]+))? hash=(?<hash>[0-9a-f]+) text=(?<text>[0-9a-f]+) -->$")]
    private static partial Regex SlotRx();

    public static ParsedDocument Parse(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        List<DocBlock> blocks = [];
        List<string> problems = [];
        StringBuilder leading = new();
        StringBuilder? trailing = null;
        DocBlock? previous = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var item = ItemRx().Match(line);
            var generated = GeneratedRx().Match(line);
            if (!item.Success && !generated.Success)
            {
                if (line.StartsWith("<!-- dblens:", StringComparison.Ordinal) && line != End)
                    problems.Add($"line {i + 1}: a dblens marker outside a region, or one that is not well formed");
                (trailing ?? leading).AppendLine(line);
                continue;
            }

            if (previous is not null) blocks[^1] = previous with { Trailing = Trim(trailing!.ToString()) };

            var isItem = item.Success;
            var match = isItem ? item : generated;
            var startLine = i + 1;
            var id = Unescape(match.Groups["id"].Value);
            StringBuilder body = new();
            DocSlot? slot = null;
            StringBuilder? slotText = null;
            var closed = false;

            for (i++; i < lines.Length; i++)
            {
                if (lines[i] == End)
                {
                    closed = true;
                    break;
                }

                var slotMatch = SlotRx().Match(lines[i]);
                if (slotMatch.Success && isItem && slot is null)
                {
                    slot = new DocSlot(
                        Unescape(slotMatch.Groups["id"].Value), int.Parse(slotMatch.Groups["max"].Value, System.Globalization.CultureInfo.InvariantCulture),
                        slotMatch.Groups["ev"].Value, slotMatch.Groups["was"].Success ? slotMatch.Groups["was"].Value : null,
                        slotMatch.Groups["hash"].Value, slotMatch.Groups["text"].Value, "");
                    slotText = new StringBuilder();
                    continue;
                }

                (slotText ?? body).AppendLine(lines[i]);
            }

            if (!closed) problems.Add($"line {startLine}: the region of {id} is never closed");
            if (slot is not null) slot = slot with { Text = Trim(slotText!.ToString()) };

            previous = new DocBlock(
                isItem ? DocBlockKind.Item : DocBlockKind.Generated, id, isItem ? match.Groups["hash"].Value : "", match.Groups["gen"].Value,
                Trim(body.ToString()), slot, "", startLine);
            blocks.Add(previous);
            trailing = new StringBuilder();
        }

        if (previous is not null) blocks[^1] = previous with { Trailing = Trim(trailing!.ToString()) };
        return new ParsedDocument(Trim(leading.ToString()), blocks, problems);
    }

    public static string Render(ParsedDocument document)
    {
        StringBuilder text = new();
        if (document.Leading.Length > 0) text.Append(document.Leading).Append("\n\n");

        foreach (var block in document.Blocks)
        {
            text.Append(block.Kind == DocBlockKind.Item ? ItemMarker(block.Id, block.Hash, block.Gen) : GeneratedMarker(block.Id, block.Gen)).Append('\n');
            if (block.Generated.Length > 0) text.Append(block.Generated).Append('\n');
            if (block.Slot is { } slot)
            {
                text.Append(SlotMarker(slot)).Append('\n');
                if (slot.Text.Length > 0) text.Append(slot.Text).Append('\n');
            }

            text.Append(End).Append('\n');
            text.Append('\n');
            if (block.Trailing.Length > 0) text.Append(block.Trailing).Append("\n\n");
        }

        return text.ToString().TrimEnd('\n') + "\n";
    }

    private static string Trim(string text) => text.Trim('\n', '\r');
}
