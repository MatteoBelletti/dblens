using System.ComponentModel;
using DbLens.Docs.Documents;
using DbLens.Docs.Inventory;
using ModelContextProtocol.Server;

namespace DbLens.Docs.Mcp;

/// <summary>The inventory as a model reads it: no empty attribute, the source as one text. Without a filter, only the counts.</summary>
public sealed record InventoryView(int Total, IReadOnlyDictionary<string, int>? ByKind, IReadOnlyList<ItemView>? Items, IReadOnlyList<CoverageNote> Coverage);

public sealed record ItemView(string Id, string Name, string Evidence, string Source, IReadOnlyDictionary<string, string> Attributes, string? Reason, IReadOnlyList<string>? AlsoAt);

/// <param name="Language">The language the words are written in: the one of the documents (<c>it</c> or <c>en</c>).</param>
public sealed record SlotList(int Total, int Returned, IReadOnlyList<SlotInfo> Slots, string Note, string Language);

/// <summary>The issues of a check, one entry per code, severity and file: <c>slot-empty</c> for a hundred slots is one line, not a hundred.</summary>
public sealed record CoverageView(bool Ok, int Errors, int Warnings, IReadOnlyList<IssueGroup> Issues);

public sealed record IssueGroup(string Code, string Severity, string File, IReadOnlyList<string> Ids, string Message);

public sealed record SlotWritten(string Id, string File, string Evidence, int Lines);

[McpServerToolType]
public sealed class DocsTools(DocsWorkspace workspace)
{
    private const int MaxItems = 200;

    [McpServerTool(ReadOnly = true), Description(
        "The inventory of the .NET solution this server was started on, read from syntax: projects, DbContexts, entities, endpoints, configuration keys, HTTP clients, background services, and the overview (components, the events that cross them, the links between components, and candidate flows), each with its source file and line. " +
        "IMPORTANT: nothing is compiled or run, so a route built at run time or a convention-based controller is named in coverage, not guessed; links and flows are deductions from names (evidence Inferred). Configuration and compose values are never read. " +
        "Without kind or idPrefix it returns only the counts per kind; ask for one kind (at most 200 items come back).")]
    public InventoryView GetProjectInventory(
        [Description("Only items of this kind (Project, Context, Entity, Endpoint, Config, HttpClient, HostedService, Component, Event, Link, Flow).")]
        string? kind = null,
        [Description("Only items whose id starts with this text, e.g. 'endpoint:GET /orders'.")]
        string? idPrefix = null)
    {
        var (inventory, _) = workspace.Code();
        ItemKind? wanted = null;
        if (kind is { Length: > 0 })
            wanted = Enum.GetValues<ItemKind>().Cast<ItemKind?>().FirstOrDefault(k => string.Equals(k.ToString(), kind, StringComparison.OrdinalIgnoreCase))
                     ?? throw new ArgumentException($"Unknown kind {kind}; one of {string.Join(", ", Enum.GetNames<ItemKind>())}.");

        var items = inventory.Items
            .Where(i => (wanted is null || i.Kind == wanted) && (idPrefix is not { Length: > 0 } || i.Id.StartsWith(idPrefix, StringComparison.Ordinal)))
            .ToList();
        if (wanted is null && idPrefix is not { Length: > 0 })
            return new InventoryView(items.Count, items.GroupBy(i => i.Kind).ToDictionary(g => g.Key.ToString(), g => g.Count()), null, inventory.Coverage);

        return new InventoryView(items.Count, null, [.. items.Take(MaxItems).Select(i => new ItemView(
            i.Id, i.Name, i.Evidence.ToString(), $"{i.Source.File}:{i.Source.Line}", ItemHash.Shown(i).ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal), i.Reason,
            i.AlsoAt.Count > 0 ? [.. i.AlsoAt.Select(a => $"{a.File}:{a.Line}")] : null))], inventory.Coverage);
    }

    [McpServerTool(ReadOnly = true), Description(
        "The slots of the project documents where words may be written, each with the facts of the item it is about and who wrote what is there. state: empty, stale (the item changed since) or current. " +
        "IMPORTANT: a slot with protected=true holds a person's words and must not be replaced. Run dblens-docs scaffold first if a document or a slot is missing.")]
    public SlotList GetDocSlots(
        [Description("empty, stale, current or all. Default: empty and stale, the slots that need words.")]
        string state = "needed",
        [Description("Only slots whose id starts with this text.")]
        string? idPrefix = null,
        [Description("Only these slot ids, exactly (whatever their state).")]
        string[]? ids = null,
        [Description("Only id, kind, file, state and protected: no facts and no text.")]
        bool brief = false)
    {
        if (state is not ("needed" or "empty" or "stale" or "current" or "all")) throw new ArgumentException("state is one of needed, empty, stale, current, all.");

        var (inventory, sources) = workspace.Code();
        var all = SlotWriter.List(inventory, workspace.Documents(), workspace.Options, sources)
            .Where(s => idPrefix is not { Length: > 0 } || s.Id.StartsWith(idPrefix, StringComparison.Ordinal))
            .Where(s => ids is not { Length: > 0 } || ids.Contains(s.Id, StringComparer.Ordinal))
            .ToList();
        var chosen = all.Where(s => ids is { Length: > 0 } || state switch { "all" => true, "needed" => s.State != "current", _ => s.State == state })
            .Select(s => brief ? s with { Facts = new Dictionary<string, string>(), Text = "", Source = "", Name = "" } : s).ToList();
        var note = all.Count == 0 ? "No slots: the documents do not exist yet (run dblens-docs scaffold) or nothing matches." : "";
        return new SlotList(chosen.Count, Math.Min(chosen.Count, MaxItems), [.. chosen.Take(MaxItems)], note, DocLabels.Language(workspace.Options.Language));
    }

    [McpServerTool(ReadOnly = true), Description(
        "Compares the project documents with the code, as dblens-docs check does: missing items, items gone from the code, items changed, generated text edited by hand, slots too long, empty or stale. " +
        "IMPORTANT: it fixes nothing; a missing or outdated item is fixed by running dblens-docs scaffold.")]
    public CoverageView CheckDocCoverage(
        [Description("Stale slots, and regions without their slot, are errors.")]
        bool strict = false,
        [Description("Empty slots, and regions without their slot, are errors.")]
        bool requireSlots = false) =>
        CheckWithCode(strict, requireSlots);

    private CoverageView CheckWithCode(bool strict, bool requireSlots)
    {
        var (inventory, sources) = workspace.Code();
        return Grouped(DocumentCheck.Check(inventory, workspace.Documents(), workspace.Options, strict, requireSlots, sources));
    }

    // Issues whose message differs per item (a problem found in a document, a slot's line count) stay one entry each.
    private static readonly string[] OwnMessage = ["malformed", "slot-too-long", "duplicate-item", "slot-bad-reference"];

    private static CoverageView Grouped(DocCheckResult result) => new(
        result.Ok, result.Errors, result.Warnings,
        [.. result.Issues.GroupBy(i => OwnMessage.Contains(i.Code) ? (i.Code, i.Severity, i.File, i.Message) : (i.Code, i.Severity, i.File, "")).Select(g =>
            new IssueGroup(g.Key.Code, g.Key.Severity.ToString(), g.Key.File, [.. g.Select(i => i.Id).Where(id => id.Length > 0)],
                OwnMessage.Contains(g.Key.Code) || g.First().Id.Length == 0 ? g.First().Message : g.First().Message.Replace(g.First().Id, "{id}", StringComparison.Ordinal)))]);

    [McpServerTool(Destructive = false, Idempotent = true), Description(
        "Writes the words of one slot of the project documents at most the slot's max lines; a caption (2 to 4 lines) is plain text with no blank lines, a narrative slot (a `detail:` one) takes Markdown with paragraphs. The only thing this server writes. " +
        "IMPORTANT: it refuses a slot that holds a person's words, a document whose item changed since it was generated, text with an HTML comment, a `File.cs: Method` or `Type: Method` citation that is not in the code, and any id without a slot. The slot is marked inferred: a person confirms it by editing it.")]
    public SlotWritten SetDocSlot(
        [Description("The item id, as get_doc_slots lists it, e.g. 'endpoint:GET /orders/{id}'.")]
        string id,
        [Description("The words: what the item is for, in at most the slot's max lines.")]
        string text)
    {
        return workspace.Update((inventory, sources, documents) =>
        {
            var (path, document) = SlotWriter.Set(inventory, documents, workspace.Options, id, text, sources);
            return (path, document, new SlotWritten(id, path, "inferred", text.Trim().Split('\n').Length));
        });
    }
}
