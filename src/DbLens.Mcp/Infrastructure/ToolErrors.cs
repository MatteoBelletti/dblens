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
                return await next(context, ct);
            }
            catch (Exception ex) when (ex is ArgumentException or ExecutionRefusedException or DatabaseInspectionException)
            {
                return new CallToolResult
                {
                    IsError = true,
                    Content = [new TextContentBlock { Text = ex.Message }],
                };
            }
        };
}
