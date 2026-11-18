namespace DbLens.Core.Analysis;

/// <summary>Name comparisons every engine shares, so they cannot drift apart.</summary>
public static class Names
{
    /// <summary>Catalog names as the engines compare them by default: ignoring case.</summary>
    public static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>A CLR type name without its namespace: "DemoShop.Order" → "Order".</summary>
    public static string Short(string typeName) => typeName[(typeName.LastIndexOf('.') + 1)..];
}
