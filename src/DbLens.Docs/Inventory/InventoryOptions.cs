namespace DbLens.Docs.Inventory;

/// <summary>
/// What the inventory needs to know about a solution that the code does not say: which base types mark an event,
/// which generic methods produce one, and which components to merge. The defaults cover the event bus of eShop and
/// the MassTransit-style contracts; <c>dblens-docs.json</c> adds to them.
/// </summary>
public sealed record InventoryOptions
{
    public static InventoryOptions Default { get; } = new();

    /// <summary>A type whose base list names one of these is an event.</summary>
    public IReadOnlyList<string> EventBases { get; init; } = ["IntegrationEvent", "IEvent", "IDomainEvent"];

    /// <summary>A generic call of one of these methods with an event as its type argument produces it (<c>new E</c> always does).</summary>
    public IReadOnlyList<string> ProducingMethods { get; init; } = ["RaiseEvent", "Publish", "PublishAsync", "Send", "AddAndSaveEventAsync"];

    /// <summary>Components to join under one name: the target name, and the names (as the inventory gives them) it absorbs.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Merge { get; init; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
}
