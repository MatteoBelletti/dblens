using DbLens.Docs.Inventory;

namespace DbLens.Docs.Documents;

public enum IssueSeverity
{
    Warning,
    Error,
}

/// <param name="Code">missing-item, orphan-item, outdated-item, generated-edited, duplicate-item, malformed, slot-too-long, slot-missing, slot-empty, slot-stale, slot-unmarked-edit,
/// slot-bad-reference (cites code that is not there), slot-stale-reference (the cited code changed), slot-no-unverified-section.</param>
public sealed record DocIssue(string Code, IssueSeverity Severity, string Id, string File, string Message);

public sealed record DocCheckResult(IReadOnlyList<DocIssue> Issues)
{
    public bool Ok => Issues.All(i => i.Severity != IssueSeverity.Error);

    public int Errors => Issues.Count(i => i.Severity == IssueSeverity.Error);

    public int Warnings => Issues.Count(i => i.Severity == IssueSeverity.Warning);
}

/// <summary>
/// Compares the documents with the inventory. Fails when an item has no region, a region has no item, an
/// item changed since its region was generated, or a generated region was edited by hand; a slot too long
/// fails too. A slot that is empty or stale is a warning, and an error with the options that ask for it.
/// </summary>
public static class DocumentCheck
{
    public static DocCheckResult Check(
        ProjectInventory inventory, IReadOnlyDictionary<string, string> documents, DocsOptions options, bool strict = false, bool requireSlots = false,
        IReadOnlyList<SourceFile>? sources = null)
    {
        var code = sources is null ? null : new ProseReferences(sources);
        var present = DocPlan.Present(documents.Keys, options);
        var plan = DocPlan.For(inventory, options, present);
        List<DocIssue> issues = [];

        Dictionary<string, (string File, DocBlock Block)> found = new(StringComparer.Ordinal);
        foreach (var file in present)
        {
            var path = $"{options.Folder}/{file}";
            if (!documents.TryGetValue(path, out var text)) continue;

            var parsed = DocumentModel.Parse(text);
            issues.AddRange(parsed.Problems.Select(p => new DocIssue("malformed", IssueSeverity.Error, "", path, p)));

            foreach (var block in parsed.Blocks)
            {
                if (!found.TryAdd(block.Id, (path, block)))
                    issues.Add(new DocIssue("duplicate-item", IssueSeverity.Error, block.Id, path, $"{block.Id} has more than one region (also in {found[block.Id].File})."));
            }
        }

        var expected = plan.SelectMany(p => p.Value.Select(d => (File: $"{options.Folder}/{p.Key}", Desired: d))).ToList();

        foreach (var (file, desired) in expected)
        {
            if (!found.TryGetValue(desired.Id, out var existing))
            {
                issues.Add(new DocIssue("missing-item", IssueSeverity.Error, desired.Id, file, $"{desired.Id} has no region in the documents: run dblens-docs scaffold."));
                continue;
            }

            var (path, block) = existing;
            if (ItemHash.OfText(block.Generated) != block.Gen)
                issues.Add(new DocIssue("generated-edited", IssueSeverity.Error, desired.Id, path, $"The generated text of {desired.Id} was edited by hand: scaffold rewrites it, put your words in its slot."));
            else if (block.Hash != desired.Hash || block.Gen != ItemHash.OfText(desired.Generated))
                issues.Add(new DocIssue("outdated-item", IssueSeverity.Error, desired.Id, path, $"{desired.Id} changed in the code since the document was generated: run dblens-docs scaffold."));

            if (block.Slot is { } slot) issues.AddRange(CheckSlot(slot, desired.Hash, path, strict, requireSlots, code));
            else if (desired.MaxLines is not null)
                issues.Add(new DocIssue("slot-missing", requireSlots || strict ? IssueSeverity.Error : IssueSeverity.Warning, desired.Id, path, $"{desired.Id} has no slot in its region: run dblens-docs scaffold."));
        }

        var wanted = expected.Select(e => e.Desired.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, (path, _)) in found.Where(f => !wanted.Contains(f.Key)))
            issues.Add(new DocIssue("orphan-item", IssueSeverity.Error, id, path, $"{id} is in the documents but no longer in the code: run dblens-docs scaffold."));

        return new DocCheckResult([.. issues.OrderBy(i => i.File, StringComparer.Ordinal).ThenBy(i => i.Id, StringComparer.Ordinal).ThenBy(i => i.Code, StringComparer.Ordinal)]);
    }

    private static readonly string[] UnverifiedHeadings = [DocLabels.For("en")["unverified"], DocLabels.For("it")["unverified"]];

    private static IEnumerable<DocIssue> CheckSlot(DocSlot slot, string itemHash, string file, bool strict, bool requireSlots, ProseReferences? code)
    {
        var lines = slot.Text.Length == 0 ? 0 : slot.Text.Split('\n').Length;
        if (lines > slot.MaxLines)
            yield return new DocIssue("slot-too-long", IssueSeverity.Error, slot.Id, file, $"The slot of {slot.Id} has {lines} lines; at most {slot.MaxLines}.");

        if (slot.Text.Length == 0)
            yield return new DocIssue("slot-empty", requireSlots ? IssueSeverity.Error : IssueSeverity.Warning, slot.Id, file, $"The slot of {slot.Id} is empty.");
        else if (slot.Evidence == "stale" || slot.ItemHash != itemHash)
            yield return new DocIssue("slot-stale", strict ? IssueSeverity.Error : IssueSeverity.Warning, slot.Id, file, $"{slot.Id} changed after its slot was written: read it again.");
        else if (slot.Evidence == "inferred" && ItemHash.OfText(slot.Text) != slot.TextHash)
            yield return new DocIssue("slot-unmarked-edit", IssueSeverity.Warning, slot.Id, file, $"The slot of {slot.Id} was edited by hand: scaffold will mark it as human.");

        if (slot.Text.Length == 0) yield break;

        // The words about a component say what was not looked at: a page without it reads as complete.
        if (slot.Id.StartsWith("detail:", StringComparison.Ordinal) && !HasUnverifiedSection(slot.Text))
            yield return new DocIssue("slot-no-unverified-section", strict ? IssueSeverity.Error : IssueSeverity.Warning, slot.Id, file, $"The slot of {slot.Id} has no \"{UnverifiedHeadings[0]}\" section: say what was not read.");

        if (code is null) yield break;

        var (missing, _) = code.Check(slot.Text);
        foreach (var reference in missing)
            yield return new DocIssue("slot-bad-reference", strict ? IssueSeverity.Error : IssueSeverity.Warning, slot.Id, file, $"The slot of {slot.Id} cites `{reference}`, which is not in the code.");

        if (SlotWriter.CitedCodeChanged(slot, code))
            yield return new DocIssue("slot-stale-reference", strict ? IssueSeverity.Error : IssueSeverity.Warning, slot.Id, file, $"The code cited by the slot of {slot.Id} changed after its words were written: read it again.");
    }

    private static bool HasUnverifiedSection(string text) =>
        text.Split('\n').Any(l => l.StartsWith('#') && UnverifiedHeadings.Contains(l.TrimStart('#', ' ').Trim(), StringComparer.OrdinalIgnoreCase));
}
