using DbLens.Core.Models;

namespace DbLens.Mcp.Infrastructure;

/// <summary>
/// The tool call in progress, opened by <see cref="ToolErrors.Filter"/>: which arguments it sent
/// (a parameter's schema default, dialect = PostgreSql, is part of the tool contract, so only the
/// call says whether it was passed) and the database it reached, which the filter adds to the answer.
/// </summary>
public sealed class ToolCall
{
    private static readonly AsyncLocal<ToolCall?> CurrentCall = new();

    private readonly HashSet<string> _arguments;

    private ToolCall(IEnumerable<string> arguments) => _arguments = new(arguments, StringComparer.Ordinal);

    /// <summary>The database the call reached; set once its inspector is resolved.</summary>
    public Target? Target { get; set; }

    public static ToolCall? Current => CurrentCall.Value;

    /// <summary>Whether the call in progress passed the argument; outside a tool call (C# callers), true.</summary>
    public static bool Has(string name) => CurrentCall.Value?._arguments.Contains(name) ?? true;

    /// <summary>Opens the call for the rest of the caller's async flow: an async caller's own return restores the previous one.</summary>
    public static ToolCall Begin(IEnumerable<string> arguments) => CurrentCall.Value = new ToolCall(arguments);
}
