using System.Text.Json;
using System.Text.Json.Nodes;
using DbLens.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Infrastructure;

/// <summary>
/// The SDK replaces an exception's message with a generic "An error occurred", which would hide a
/// guard rejection or a refusal from the model that needs to read it. Exceptions dblens raises for
/// the caller are turned into readable tool errors; anything else keeps the generic message.
/// </summary>
public static class ToolErrors
{
    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, ct) =>
        {
            var call = ToolCall.Begin(context?.Params?.Arguments?.Keys ?? []);
            try
            {
                var result = await next(context!, ct);
                return call.Target is { } target ? WithTarget(result, target) : result;
            }
            catch (Exception ex) when (ex is ArgumentException or ExecutionRefusedException or DatabaseInspectionException)
            {
                return Error(ex.Message);
            }
            catch (JsonException ex)
            {
                // An argument of the wrong type or an unknown enum value: say which, so the caller can fix it.
                return Error($"Invalid arguments: {ex.Message}");
            }
        };

    /// <summary>Adds "target" to a JSON answer, so every result names the database it describes.</summary>
    private static CallToolResult WithTarget(CallToolResult result, Target target)
    {
        if (result.IsError == true) return result;

        var node = JsonSerializer.SerializeToNode(target, McpJsonUtilities.DefaultOptions);
        foreach (var block in result.Content.OfType<TextContentBlock>())
            if (Parse(block.Text) is JsonObject answer)
            {
                answer["target"] = node!.DeepClone();
                block.Text = answer.ToJsonString(McpJsonUtilities.DefaultOptions);
            }
        return result;
    }

    private static JsonNode? Parse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
