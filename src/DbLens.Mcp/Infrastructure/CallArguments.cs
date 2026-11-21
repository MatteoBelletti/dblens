namespace DbLens.Mcp.Infrastructure;

/// <summary>
/// The argument names of the tool call in progress. A parameter's schema default (dialect =
/// PostgreSql) is part of the tool contract, so only the call itself says whether it was passed.
/// </summary>
public static class CallArguments
{
    private static readonly AsyncLocal<IReadOnlySet<string>?> Current = new();

    /// <summary>Whether the call in progress passed the argument; outside a tool call (C# callers), true.</summary>
    public static bool Has(string name) => Current.Value?.Contains(name) ?? true;

    public static IDisposable Begin(IEnumerable<string> names)
    {
        var previous = Current.Value;
        Current.Value = new HashSet<string>(names, StringComparer.Ordinal);
        return new Restore(previous);
    }

    private sealed class Restore(IReadOnlySet<string>? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
