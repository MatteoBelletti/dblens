using DbLens.Docs.Cli;
using DbLens.Docs.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (args is ["mcp", .. var rest])
{
    if (rest.Length > 1 || rest.Any(a => a.StartsWith("--", StringComparison.Ordinal)))
    {
        Console.Error.WriteLine("dblens-docs mcp [root]: serve the documents of one solution over MCP (stdio).");
        return 2;
    }

    var builder = Host.CreateApplicationBuilder(args: []);
    // Logging goes to stderr only: stdout belongs to the MCP protocol.
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
    try
    {
        builder.Services.AddDocsServer(rest.Length == 1 ? rest[0] : Directory.GetCurrentDirectory()).WithStdioServerTransport();
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"dblens-docs: {ex.Message}");
        return 2;
    }

    await builder.Build().RunAsync();
    return 0;
}

return DocsCli.Run(args, Console.Out, Console.Error, Directory.GetCurrentDirectory());
