using DbLens.Mcp.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (Environment.GetEnvironmentVariable("MCP_TRANSPORT") == "sse")
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
    builder.WebHost.UseUrls("http://localhost:5050");
    Configure(builder.Logging, builder.Services, builder.Configuration).WithHttpTransport();

    var app = builder.Build();
    app.MapMcp("/mcp");
    await app.RunAsync();
}
else
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
    Configure(builder.Logging, builder.Services, builder.Configuration).WithStdioServerTransport();

    await builder.Build().RunAsync();
}

static IMcpServerBuilder Configure(ILoggingBuilder logging, IServiceCollection services, IConfiguration configuration)
{
    // Logging goes to stderr only: stdout belongs to the MCP protocol.
    logging.ClearProviders();
    logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    return services.AddDbLensServer(configuration);
}
