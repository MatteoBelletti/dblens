using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DbLens.Mcp.Infrastructure;

/// <summary>Where a call's database came from.</summary>
public enum TargetSource
{
    /// <summary><c>ConnectionStrings:&lt;dialect&gt;</c> in the server's configuration.</summary>
    Configured,

    /// <summary>The caller's <c>connectionString</c> parameter.</summary>
    Parameter,

    /// <summary>The local demo database, because <c>DbLens:DemoFallback</c> is on and nothing is configured.</summary>
    DemoDefault,
}

public sealed record ConfiguredDatabase(DatabaseDialect Dialect, string ConnectionString, TargetSource Source);

/// <summary>
/// The database the server uses for each dialect when a call passes no connection string:
/// <c>ConnectionStrings:&lt;dialect&gt;</c>, else the local demo only when <c>DbLens:DemoFallback</c>
/// is on. An empty value — an unset <c>.mcpb</c> setting — counts as not configured.
/// </summary>
public sealed class ConfiguredDatabases(IConfiguration configuration)
{
    public const string DemoPostgreSql =
        "Host=localhost;Port=5433;Database=dblens_demo;Username=dblens_reader;Password=readerpassword";

    public const string DemoSqlServer =
        "Server=localhost,1434;Database=dblens_demo;User Id=dblens_reader;Password=ReaderPassword123!;TrustServerCertificate=true";

    public bool DemoFallback => configuration.GetValue("DbLens:DemoFallback", false);

    public ConfiguredDatabase? For(DatabaseDialect dialect)
    {
        var configured = configuration[$"ConnectionStrings:{dialect}"];
        if (!string.IsNullOrWhiteSpace(configured)) return new(dialect, configured, TargetSource.Configured);

        return DemoFallback
            ? new(dialect, dialect == DatabaseDialect.SqlServer ? DemoSqlServer : DemoPostgreSql, TargetSource.DemoDefault)
            : null;
    }

    /// <summary>The configured database for the dialect, or the error a caller can act on.</summary>
    public ConfiguredDatabase Require(DatabaseDialect dialect) =>
        For(dialect) ?? throw new ArgumentException(
            $"No database is configured for {dialect}: pass connectionString, or set ConnectionStrings__{dialect} " +
            "in the server's environment.");
}

public static class ConfiguredDatabasesRegistration
{
    /// <summary>One inspector per dialect, keyed by it, built on first use from <see cref="ConfiguredDatabases"/>.</summary>
    public static IServiceCollection AddConfiguredDatabases(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new ConfiguredDatabases(configuration));
        foreach (var dialect in Enum.GetValues<DatabaseDialect>())
            services.AddKeyedSingleton<IDatabaseInspector>(dialect, (sp, _) =>
                sp.GetRequiredService<IDatabaseInspectorFactory>().Create(
                    dialect, sp.GetRequiredService<ConfiguredDatabases>().Require(dialect).ConnectionString));
        return services;
    }
}
