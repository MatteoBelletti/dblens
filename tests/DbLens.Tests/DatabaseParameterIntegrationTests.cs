using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using DbLens.Mcp.Tools;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbLens.Tests;

/// <summary><c>database</c> reaches another database of the configured server, with the configured login and no password in the call.</summary>
public abstract class DatabaseParameterTestsBase(IDatabaseFixture fixture)
{
    protected abstract DatabaseDialect Dialect { get; }

    /// <summary>A database of the server other than the demo one, that the reader login can open.</summary>
    protected abstract string OtherDatabase { get; }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaWithDatabase_ReadsTheOtherDatabase()
    {
        Skip.If(!fixture.IsReachable, TestSkip.Reason);
        var configured = TestConnections.For(Dialect);
        var services = new ServiceCollection()
            .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(new ServerPolicy(false, false, false))
            .AddSingleton<IDatabaseInspectorFactory, DatabaseInspectorFactory>()
            .AddConfiguredDatabases(new ConfigurationBuilder().AddInMemoryCollection([new($"ConnectionStrings:{Dialect}", configured)]).Build())
            .BuildServiceProvider();

        var overview = await new GetSchemaTool(services, services.GetRequiredService<IDatabaseInspectorFactory>())
            .GetSchema(dialect: Dialect, database: OtherDatabase);

        overview.Database.Should().Be(OtherDatabase);
    }
}

public sealed class PostgresDatabaseParameterTests(PostgresFixture fixture) : DatabaseParameterTestsBase(fixture), IClassFixture<PostgresFixture>
{
    protected override DatabaseDialect Dialect => DatabaseDialect.PostgreSql;
    protected override string OtherDatabase => "postgres";
}

public sealed class SqlServerDatabaseParameterTests(SqlServerFixture fixture) : DatabaseParameterTestsBase(fixture), IClassFixture<SqlServerFixture>
{
    protected override DatabaseDialect Dialect => DatabaseDialect.SqlServer;
    protected override string OtherDatabase => "master";
}
