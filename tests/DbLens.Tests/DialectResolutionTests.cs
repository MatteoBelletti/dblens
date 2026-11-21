using System.Reflection;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using DbLens.Mcp.Tools;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using ModelContextProtocol.Server;

namespace DbLens.Tests;

/// <summary>
/// A call that names no dialect reaches the only configured one, and nothing else: two configured
/// dialects, or a connection string without its dialect, are errors rather than a guess.
/// </summary>
public sealed class DialectResolutionTests
{
    private const string Pg = "Host=pg;Database=erp";
    private const string Ss = "Server=ss;Database=erp";

    private static ConfiguredDatabases Databases(string? pg = null, string? ss = null) =>
        new(new ConfigurationBuilder().AddInMemoryCollection([
            new("ConnectionStrings:PostgreSql", pg), new("ConnectionStrings:SqlServer", ss)]).Build());

    [Theory]
    [InlineData(Pg, null, DatabaseDialect.PostgreSql)]
    [InlineData(null, Ss, DatabaseDialect.SqlServer)]
    public void NoDialect_OneConfigured_IsThatOne(string? pg, string? ss, DatabaseDialect expected) =>
        Databases(pg, ss).Resolve(DatabaseDialect.PostgreSql, dialectGiven: false, connectionString: null)
            .Should().Be(new ConfiguredDatabase(expected, pg ?? ss!, TargetSource.Configured));

    [Fact]
    public void NoDialect_BothConfigured_IsAnError() =>
        Databases(Pg, Ss).Invoking(d => d.Resolve(DatabaseDialect.PostgreSql, false, null))
            .Should().Throw<ArgumentException>().WithMessage("Both PostgreSql and SqlServer are configured: pass dialect*");

    [Fact]
    public void NoDialect_NoneConfigured_IsAnError() =>
        Databases().Invoking(d => d.Resolve(DatabaseDialect.PostgreSql, false, null))
            .Should().Throw<ArgumentException>().WithMessage("No database is configured: pass dialect and connectionString*");

    [Fact]
    public void ConnectionStringWithoutDialect_IsAnError_NeverAGuess() =>
        Databases(Pg).Invoking(d => d.Resolve(DatabaseDialect.PostgreSql, false, Ss))
            .Should().Throw<ArgumentException>().WithMessage("connectionString needs dialect*");

    [Fact]
    public void ConnectionStringWithDialect_IsTheParameter() =>
        Databases(Pg, Ss).Resolve(DatabaseDialect.SqlServer, true, "Server=other")
            .Should().Be(new ConfiguredDatabase(DatabaseDialect.SqlServer, "Server=other", TargetSource.Parameter));

    [Fact]
    public void DialectGiven_NotConfigured_IsAnError_EvenWhenTheOtherIs() =>
        Databases(Pg).Invoking(d => d.Resolve(DatabaseDialect.SqlServer, true, null))
            .Should().Throw<ArgumentException>().WithMessage("No database is configured for SqlServer*");

    [SkippableTheory]
    [InlineData(DatabaseDialect.PostgreSql, "public")]
    [InlineData(DatabaseDialect.SqlServer, "dbo")]
    [Trait("Category", "Integration")]
    public async Task OverMcp_AToolWithoutDialect_ReachesTheOnlyConfiguredOne(DatabaseDialect dialect, string schema)
    {
        Skip.If(!await TestConnections.IsReachableAsync(dialect), TestSkip.Reason);
        await using var mcp = await McpHarness.StartAsync(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{dialect}"] = TestConnections.For(dialect),
        });

        var (isError, json, text) = await mcp.CallAsync("get_schema", new Dictionary<string, object?> { ["schemaFilter"] = schema });

        isError.Should().BeFalse(text);
        json.GetProperty("dialect").GetString().Should().Be(dialect.ToString());
    }

    [Fact]
    public async Task OverMcp_BothConfigured_WithoutDialect_IsAReadableError()
    {
        await using var mcp = await McpHarness.StartAsync(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PostgreSql"] = Pg,
            ["ConnectionStrings:SqlServer"] = Ss,
        });

        var (isError, _, text) = await mcp.CallAsync("get_schema", new Dictionary<string, object?>());

        isError.Should().BeTrue();
        text.Should().StartWith("Both PostgreSql and SqlServer are configured");
    }

    [Fact]
    public async Task OverMcp_AConnectionStringWithoutDialect_IsAReadableError()
    {
        await using var mcp = await McpHarness.StartAsync(new Dictionary<string, string?> { ["ConnectionStrings:PostgreSql"] = Pg });

        var (isError, _, text) = await mcp.CallAsync("get_schema", new Dictionary<string, object?> { ["connectionString"] = Ss });

        isError.Should().BeTrue();
        text.Should().StartWith("connectionString needs dialect");
    }

    [Fact]
    public async Task OverMcp_NothingConfigured_IsAReadableError_NotTheDemo()
    {
        await using var mcp = await McpHarness.StartAsync(new Dictionary<string, string?>());

        var (isError, _, text) = await mcp.CallAsync("get_schema", new Dictionary<string, object?> { ["dialect"] = "PostgreSql" });

        isError.Should().BeTrue();
        text.Should().StartWith("No database is configured for PostgreSql");
    }

    [Fact]
    public void EveryToolWithADialect_ResolvesItThroughInspectorTool()
    {
        var tools = typeof(PingTool).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .Where(t => t.GetMethods().Any(m => m.GetParameters().Any(p => p.Name == "dialect")))
            .ToList();

        tools.Should().NotBeEmpty().And.OnlyContain(t => t.IsSubclassOf(typeof(InspectorTool)));
    }
}
