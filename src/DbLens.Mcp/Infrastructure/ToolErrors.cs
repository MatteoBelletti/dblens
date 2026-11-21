using System.Text.Json;
using DbLens.Core.Models;
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
            try
            {
                // The test harness may call the filter without a request.
                using var _ = CallArguments.Begin(context?.Params?.Arguments?.Keys ?? []);
                return await next(context!, ct);
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

    private static CallToolResult Error(string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = message }] };
}
