using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using DbLens.Mcp.Tools;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbLens.Tests;

/// <summary><c>database</c> reaches another database of the configured server, with the configured login and no password in the call.</summary>
public sealed class DatabaseParameterIntegrationTests
{
    private static async Task<DatabaseSchemaOverview?> SchemaOf(DatabaseDialect dialect, string configured, string database)
    {
        var services = new ServiceCollection()
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(new ServerPolicy(false, false, false))
            .AddSingleton<IDatabaseInspectorFactory, DatabaseInspectorFactory>()
            .AddConfiguredDatabases(new ConfigurationBuilder().AddInMemoryCollection([new($"ConnectionStrings:{dialect}", configured)]).Build())
            .BuildServiceProvider();
        var factory = services.GetRequiredService<IDatabaseInspectorFactory>();
        if (!await factory.Create(dialect, configured).TestConnectionAsync()) return null;

        return await new GetSchemaTool(services, factory).GetSchema(dialect: dialect, database: database);
    }

    [SkippableFact]
    public async Task PostgreSql_GetSchemaWithDatabase_ReadsTheOtherDatabase()
    {
        var configured = Environment.GetEnvironmentVariable("DBLENS_TEST_PG_CONNECTION")
                         ?? "Host=localhost;Port=5433;Database=dblens_demo;Username=dblens_reader;Password=readerpassword";

        var overview = await SchemaOf(DatabaseDialect.PostgreSql, configured, "postgres");

        Skip.If(overview is null, "PostgreSQL is not reachable.");
        overview!.Database.Should().Be("postgres");
    }

    [SkippableFact]
    public async Task SqlServer_GetSchemaWithDatabase_ReadsTheOtherDatabase()
    {
        var configured = Environment.GetEnvironmentVariable("DBLENS_TEST_SS_CONNECTION")
                         ?? "Server=localhost,1434;Database=dblens_demo;User Id=dblens_reader;Password=ReaderPassword123!;TrustServerCertificate=true";

        var overview = await SchemaOf(DatabaseDialect.SqlServer, configured, "master");

        Skip.If(overview is null, "SQL Server is not reachable.");
        overview!.Database.Should().Be("master");
    }
}
