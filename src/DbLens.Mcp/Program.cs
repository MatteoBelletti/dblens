using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using DbLens.Mcp.Tools;
using DbLens.Postgres;
using DbLens.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var transport = Environment.GetEnvironmentVariable("MCP_TRANSPORT") ?? "stdio";

if (transport == "sse")
{
    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls("http://localhost:5050");
    ConfigureLogging(builder.Logging);
    builder.Services
        .AddMcpServer()
        .WithHttpTransport()
        .WithToolsFromAssembly(typeof(PingTool).Assembly);
    ConfigureDatabase(builder.Services, builder.Configuration);
    var app = builder.Build();
    app.MapMcp("/mcp");
    await app.RunAsync();
}
else
{
    // Logging must go to stderr only — stdout is reserved for the MCP protocol.
    var builder = Host.CreateApplicationBuilder(args);
    ConfigureLogging(builder.Logging);
    builder.Services
        .AddMcpServer()
        .WithStdioServerTransport()
        .WithToolsFromAssembly(typeof(PingTool).Assembly);
    ConfigureDatabase(builder.Services, builder.Configuration);
    await builder.Build().RunAsync();
}

static void ConfigureLogging(ILoggingBuilder logging)
{
    logging.ClearProviders();
    logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
}

static void ConfigureDatabase(IServiceCollection services, IConfiguration configuration)
{
    var pgCs = configuration["ConnectionStrings:PostgreSql"]
        ?? "Host=localhost;Port=5433;Database=dblens_demo;Username=postgres;Password=devpassword";
    var ssCs = configuration["ConnectionStrings:SqlServer"]
        ?? "Server=localhost,1434;Database=dblens_demo;User Id=sa;Password=DevPassword123!;TrustServerCertificate=true";

    services.AddKeyedSingleton<IDatabaseInspector>(
        DatabaseDialect.PostgreSql,
        (sp, _) => new PostgresInspector(pgCs, sp.GetRequiredService<ILogger<PostgresInspector>>()));

    services.AddKeyedSingleton<IDatabaseInspector>(
        DatabaseDialect.SqlServer,
        (sp, _) => new SqlServerInspector(ssCs, sp.GetRequiredService<ILogger<SqlServerInspector>>()));

    services.AddSingleton<IDatabaseInspectorFactory, DatabaseInspectorFactory>();
}
