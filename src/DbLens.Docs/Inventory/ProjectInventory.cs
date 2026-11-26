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

    /// <summary>A macro-component: an executable with the libraries only it uses, or a library several share.</summary>
    Component,

    /// <summary>A message that crosses components, with who produces and who consumes it.</summary>
    Event,

    /// <summary>A connection between two components: an event, a call, data read, a reference.</summary>
    Link,

    /// <summary>A candidate flow: what an entry point sets in motion, hop by hop.</summary>
    Flow,

    /// <summary>The place for the narrative about one component: its detail document, when someone asks for one.</summary>
    Detail,
}

/// <summary>How an item was established: what the syntax says, or why it could not be read.</summary>
public enum ItemEvidence
{
    /// <summary>The code states it: the declaration and its text are there.</summary>
    Extracted,

    /// <summary>The element exists, but a part of it cannot be read from syntax; <see cref="InventoryItem.Reason"/> says which.</summary>
    Unresolved,

    /// <summary>A deduction from names or shapes, not a statement of the code: wrong more often than <see cref="Extracted"/>.</summary>
    Inferred,
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
    public const int CurrentSchemaVersion = 2;
}
