namespace DbLens.Docs.Inventory;

public enum ItemKind
{
    Project,
    Context,
    Entity,
    Endpoint,
    Config,
    HttpClient,
    HostedService,
}

/// <summary>How an item was established: what the syntax says, or why it could not be read.</summary>
public enum ItemEvidence
{
    /// <summary>The code states it: the declaration and its text are there.</summary>
    Extracted,

    /// <summary>The element exists, but a part of it cannot be read from syntax; <see cref="InventoryItem.Reason"/> says which.</summary>
    Unresolved,
}

/// <summary>A place in the solution: path relative to the root, with '/', and a 1-based line.</summary>
public sealed record SourceLocation(string File, int Line);

/// <summary>
/// One thing to document. <see cref="Id"/> is stable: the same files give the same id on every run, and
/// an unrelated edit does not change it (a line number is never part of it).
/// </summary>
public sealed record InventoryItem(
    string Id,
    ItemKind Kind,
    string Name,
    ItemEvidence Evidence,
    SourceLocation Source,
    IReadOnlyDictionary<string, string> Attributes,
    /// <summary>Other places that declare or read it, in order. Empty when there is one.</summary>
    IReadOnlyList<SourceLocation> AlsoAt,
    /// <summary>Why an <see cref="ItemEvidence.Unresolved"/> item is, in one sentence. Null otherwise.</summary>
    string? Reason);

/// <summary>What the inventory did not see: an empty list of items is never an all-clear.</summary>
public sealed record CoverageNote(string Area, string Note, int Count);

public sealed record ProjectInventory(
    int SchemaVersion,
    IReadOnlyList<InventoryItem> Items,
    IReadOnlyList<CoverageNote> Coverage)
{
    public const int CurrentSchemaVersion = 1;
}
