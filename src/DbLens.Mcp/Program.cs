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
        .WithToolsFromAssembly(typeof(PingTool).Assembly)
        .WithRequestFilters(filters => filters.AddCallToolFilter(ToolErrors.Filter));
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
        .WithToolsFromAssembly(typeof(PingTool).Assembly)
        .WithRequestFilters(filters => filters.AddCallToolFilter(ToolErrors.Filter));
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
    var pgCs = NonEmpty(configuration["ConnectionStrings:PostgreSql"])
        ?? "Host=localhost;Port=5433;Database=dblens_demo;Username=dblens_reader;Password=readerpassword";
    var ssCs = NonEmpty(configuration["ConnectionStrings:SqlServer"])
        ?? "Server=localhost,1434;Database=dblens_demo;User Id=dblens_reader;Password=ReaderPassword123!;TrustServerCertificate=true";

    var policy = ServerPolicy.From(configuration);
    services.AddSingleton(policy);

    services.AddKeyedSingleton<IDatabaseInspector>(
        DatabaseDialect.PostgreSql,
        (sp, _) => new PostgresInspector(pgCs, sp.GetRequiredService<ILogger<PostgresInspector>>())
        {
            AllowWriteCapableLogin = policy.AllowWriteCapableLogin,
            ReadOnlyLoginOnLocalhost = policy.ReadOnlyLoginOnLocalhost,
        });

    services.AddKeyedSingleton<IDatabaseInspector>(
        DatabaseDialect.SqlServer,
        (sp, _) => new SqlServerInspector(ssCs, sp.GetRequiredService<ILogger<SqlServerInspector>>())
        {
            AllowWriteCapableLogin = policy.AllowWriteCapableLogin,
            ReadOnlyLoginOnLocalhost = policy.ReadOnlyLoginOnLocalhost,
        });

    services.AddSingleton<IDatabaseInspectorFactory, DatabaseInspectorFactory>();
    services.AddSingleton(new CertifiedQueryStore(
        configuration["DbLens:CertifiedQueriesPath"] ?? CertifiedQueryStore.DefaultPath()));
}

// An unset .mcpb user setting arrives as an empty environment variable, which must mean "use the default".
static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
