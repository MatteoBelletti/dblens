using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using DbLens.Mcp.Tools;
using DbLens.Postgres;
using DbLens.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Logging must go to stderr only — stdout is reserved for the MCP protocol.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options =>
    options.LogToStandardErrorThreshold = LogLevel.Trace);

// MCP server over stdio.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(PingTool).Assembly);

// Database inspectors registered as keyed singletons by dialect.
var pgCs = builder.Configuration["ConnectionStrings:PostgreSql"]
    ?? "Host=localhost;Port=5433;Database=dblens_demo;Username=postgres;Password=devpassword";
var ssCs = builder.Configuration["ConnectionStrings:SqlServer"]
    ?? "Server=localhost,1434;Database=dblens_demo;User Id=sa;Password=DevPassword123!;TrustServerCertificate=true";

builder.Services.AddKeyedSingleton<IDatabaseInspector>(
    DatabaseDialect.PostgreSql,
    (sp, _) => new PostgresInspector(pgCs, sp.GetRequiredService<ILogger<PostgresInspector>>()));

builder.Services.AddKeyedSingleton<IDatabaseInspector>(
    DatabaseDialect.SqlServer,
    (sp, _) => new SqlServerInspector(ssCs, sp.GetRequiredService<ILogger<SqlServerInspector>>()));

// Factory for on-demand inspectors from a user-supplied connection string.
builder.Services.AddSingleton<IDatabaseInspectorFactory, DatabaseInspectorFactory>();

await builder.Build().RunAsync();
