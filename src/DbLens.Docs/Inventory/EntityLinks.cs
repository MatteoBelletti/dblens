using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbLens.Docs.Inventory;

/// <summary>
/// Which entities an endpoint's code names. A heuristic over names: an identifier of the handler (its
/// signature and its body) that is the simple name of an entity of the model. A handler that delegates to
/// a service, or reaches an entity through a property or <c>var</c>, shows none: the links are signals,
/// never a complete map.
/// </summary>
internal static class EntityLinks
{
    /// <summary>The simple name of each entity, and the entities (as "Context.Simple") that carry it.</summary>
    public static Dictionary<string, List<string>> Index(IReadOnlyList<InventoryItem> entities)
    {
        Dictionary<string, List<string>> index = new(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var simple = Simple(entity.Name);
            if (!index.TryGetValue(simple, out var list)) index[simple] = list = [];
            list.Add($"{entity.Attributes["context"]}.{simple}");
        }

        return index;
    }

    /// <summary>The entities the handler names, sorted; a name two contexts share is listed with each context.</summary>
    public static IReadOnlyList<string> Touched(SyntaxNode? body, Dictionary<string, List<string>> index)
    {
        if (body is null || index.Count == 0) return [];

        HashSet<string> found = new(StringComparer.Ordinal);
        foreach (var identifier in body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            // "x.Order" is a member of something else; "Order" alone, or "Results.Ok<Order>", names the type.
            if (identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier) continue;
            if (identifier.Parent is MemberBindingExpressionSyntax) continue;
            if (!index.TryGetValue(identifier.Identifier.Text, out var candidates)) continue;

            foreach (var entity in candidates.Count == 1 ? [identifier.Identifier.Text] : candidates)
                found.Add(entity);
        }

        return [.. found.Order(StringComparer.Ordinal)];
    }

    public static string Simple(string clrName) => clrName[(clrName.LastIndexOfAny(['.', '+']) + 1)..];
}
