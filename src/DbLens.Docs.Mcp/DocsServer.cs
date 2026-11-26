using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DbLens.Docs.Mcp;

public static class DocsServer
{
    /// <summary>The server but its transport: the dblens-docs command adds stdio, the tests a pipe.</summary>
    public static IMcpServerBuilder AddDocsServer(this IServiceCollection services, string root)
    {
        services.AddSingleton(new DocsWorkspace(root));
        return services
            .AddMcpServer()
            .WithTools<DocsTools>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(Errors));
    }

    // The SDK hides the text of every exception; a refusal has to reach the model that can act on it.
    private static McpRequestHandler<CallToolRequestParams, CallToolResult> Errors(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, ct) =>
        {
            try
            {
                return await next(context, ct);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ex.Message }] };
            }
        };
}
