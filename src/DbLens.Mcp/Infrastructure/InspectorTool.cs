using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DbLens.Mcp.Infrastructure;

/// <summary>
/// Base for every tool that talks to a database: resolves the inspector a call should use.
/// A caller-supplied connection string gets an ad-hoc inspector; otherwise the configured
/// keyed singleton for the dialect is used.
/// </summary>
public abstract class InspectorTool(IServiceProvider services, IDatabaseInspectorFactory factory)
{
    protected IDatabaseInspector ResolveInspector(DatabaseDialect dialect, string? connectionString) =>
        connectionString is not null
            ? factory.Create(dialect, connectionString)
            : ((IKeyedServiceProvider)services).GetRequiredKeyedService<IDatabaseInspector>(dialect);
}
