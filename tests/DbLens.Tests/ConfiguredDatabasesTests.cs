using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using DbLens.Mcp.Tools;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbLens.Tests;

/// <summary>No configured database means an error the caller can act on, never the demo by surprise.</summary>
public sealed class ConfiguredDatabasesTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.SqlServer)]
    public void NothingConfigured_IsAClearError_NotTheDemo(DatabaseDialect dialect)
    {
        var databases = new ConfiguredDatabases(Config());

        databases.For(dialect).Should().BeNull();
        databases.Invoking(d => d.Require(dialect)).Should().Throw<ArgumentException>()
            .WithMessage($"No database is configured for {dialect}: pass connectionString, or set ConnectionStrings__{dialect}*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyValue_AsAnUnsetMcpbSettingSendsIt_IsNotConfigured(string value)
    {
        var databases = new ConfiguredDatabases(Config(("ConnectionStrings:PostgreSql", value)));

        databases.For(DatabaseDialect.PostgreSql).Should().BeNull();
    }

    [Fact]
    public void DemoFallback_GivesTheDemo_MarkedAsSuch()
    {
        var databases = new ConfiguredDatabases(Config(("DbLens:DemoFallback", "true"), ("ConnectionStrings:SqlServer", "")));

        databases.For(DatabaseDialect.PostgreSql).Should().Be(
            new ConfiguredDatabase(DatabaseDialect.PostgreSql, ConfiguredDatabases.DemoPostgreSql, TargetSource.DemoDefault));
        databases.For(DatabaseDialect.SqlServer)!.Source.Should().Be(TargetSource.DemoDefault);
    }

    [Fact]
    public void AConfiguredValue_WinsOverTheDemo()
    {
        var databases = new ConfiguredDatabases(Config(("DbLens:DemoFallback", "true"), ("ConnectionStrings:PostgreSql", "Host=db;Database=erp")));

        databases.For(DatabaseDialect.PostgreSql).Should().Be(
            new ConfiguredDatabase(DatabaseDialect.PostgreSql, "Host=db;Database=erp", TargetSource.Configured));
    }

    [Fact]
    public async Task ATool_WithNothingConfigured_ReturnsTheErrorToTheCaller()
    {
        var services = new ServiceCollection()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton(new ServerPolicy(false, false, false))
            .AddSingleton<IDatabaseInspectorFactory, DatabaseInspectorFactory>()
            .AddConfiguredDatabases(Config())
            .BuildServiceProvider();
        var tool = new GetSchemaTool(services, services.GetRequiredService<IDatabaseInspectorFactory>());

        var call = () => tool.GetSchema(dialect: DatabaseDialect.SqlServer);

        await call.Should().ThrowAsync<ArgumentException>().WithMessage("No database is configured for SqlServer*");
    }
}
