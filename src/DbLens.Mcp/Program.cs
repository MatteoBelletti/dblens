using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using DbLens.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (Environment.GetEnvironmentVariable("MCP_TRANSPORT") == "sse")
{
    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls("http://localhost:5050");
    Configure(builder.Logging, builder.Services, builder.Configuration).WithHttpTransport();

    var app = builder.Build();
    app.MapMcp("/mcp");
    await app.RunAsync();
}
else
{
    var builder = Host.CreateApplicationBuilder(args);
    Configure(builder.Logging, builder.Services, builder.Configuration).WithStdioServerTransport();

    await builder.Build().RunAsync();
}

static IMcpServerBuilder Configure(ILoggingBuilder logging, IServiceCollection services, IConfiguration configuration)
{
    // Logging goes to stderr only: stdout belongs to the MCP protocol.
    logging.ClearProviders();
    logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

    services.AddSingleton(ServerPolicy.From(configuration));
    services.AddSingleton<IDatabaseInspectorFactory, DatabaseInspectorFactory>();
    services.AddSingleton(new CertifiedQueryStore(
        configuration["DbLens:CertifiedQueriesPath"] ?? CertifiedQueryStore.DefaultPath()));

    AddDefaultInspector(DatabaseDialect.PostgreSql,
        "Host=localhost;Port=5433;Database=dblens_demo;Username=dblens_reader;Password=readerpassword");
    AddDefaultInspector(DatabaseDialect.SqlServer,
        "Server=localhost,1434;Database=dblens_demo;User Id=dblens_reader;Password=ReaderPassword123!;TrustServerCertificate=true");

    return services
        .AddMcpServer()
        .WithToolsFromAssembly(typeof(PingTool).Assembly)
        .WithRequestFilters(filters => filters.AddCallToolFilter(ToolErrors.Filter));

    // An unset .mcpb user setting arrives as an empty value, which must mean "use the default".
    void AddDefaultInspector(DatabaseDialect dialect, string fallback)
    {
        var configured = configuration[$"ConnectionStrings:{dialect}"];
        var connectionString = string.IsNullOrWhiteSpace(configured) ? fallback : configured;

        services.AddKeyedSingleton<IDatabaseInspector>(dialect, (sp, _) =>
            sp.GetRequiredService<IDatabaseInspectorFactory>().Create(dialect, connectionString));
    }
}
