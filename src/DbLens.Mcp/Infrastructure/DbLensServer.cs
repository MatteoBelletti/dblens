using DbLens.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbLens.Mcp.Infrastructure;

public static class DbLensServer
{
    /// <summary>The whole server but its transport: Program adds stdio or HTTP, the tests a pipe.</summary>
    public static IMcpServerBuilder AddDbLensServer(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(ServerPolicy.From(configuration));
        services.AddSingleton<IDatabaseInspectorFactory, DatabaseInspectorFactory>();
        services.AddSingleton(new CertifiedQueryStore(
            configuration["DbLens:CertifiedQueriesPath"] ?? CertifiedQueryStore.DefaultPath()));
        services.AddConfiguredDatabases(configuration);

        return services
            .AddMcpServer()
            .WithToolsFromAssembly(typeof(PingTool).Assembly)
            .WithRequestFilters(filters => filters.AddCallToolFilter(ToolErrors.Filter));
    }
}
