using System.Text.Json;
using DbLens.Core.Models;
using DbLens.Postgres;
using DbLens.SqlServer;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbLens.Tests;

/// <summary>Every answer names the database it describes, read from the connection string without its password.</summary>
public sealed class TargetTests
{
    [Theory]
    [InlineData("Host=db.example.com;Port=6543;Database=erp;Username=reader;Password=s3cret", "db.example.com", 6543, "erp", "reader", false)]
    [InlineData("Host=localhost;Database=erp;Username=reader;Password=s3cret", "localhost", 5432, "erp", "reader", true)]
    [InlineData("Host=h1,h2;Database=erp;Username=reader;Password=s3cret", "h1,h2", 5432, "erp", "reader", false)]
    public void PostgreSql_FromTheBuilder(string cs, string host, int port, string database, string user, bool local) =>
        new PostgresInspector(cs, NullLogger<PostgresInspector>.Instance).Describe(TargetSource.Parameter)
            .Should().Be(new Target(DatabaseDialect.PostgreSql, host, port, database, user, local, TargetSource.Parameter));

    [Theory]
    [InlineData("Server=tcp:db.example.com,1533;Database=erp;User Id=reader;Password=s3cret", "db.example.com", 1533, "erp", "reader", false)]
    [InlineData("Server=localhost,1434;Database=erp;User Id=reader;Password=s3cret", "localhost", 1434, "erp", "reader", true)]
    [InlineData(@"Server=.\SQLEXPRESS;Database=erp;Integrated Security=true", @".\SQLEXPRESS", null, "erp", "(integrated security)", true)]
    public void SqlServer_FromTheBuilder(string cs, string host, int? port, string database, string user, bool local) =>
        new SqlServerInspector(cs, NullLogger<SqlServerInspector>.Instance).Describe(TargetSource.Configured)
            .Should().Be(new Target(DatabaseDialect.SqlServer, host, port, database, user, local, TargetSource.Configured));

    [SkippableTheory]
    [InlineData(DatabaseDialect.PostgreSql)]
    [InlineData(DatabaseDialect.SqlServer)]
    [Trait("Category", "Integration")]
    public async Task Ping_Connects_AndNamesTheTarget_WithoutThePassword(DatabaseDialect dialect)
    {
        Skip.If(!await TestConnections.IsReachableAsync(dialect), TestSkip.Reason);
        await using var mcp = await McpHarness.StartAsync(new Dictionary<string, string?> { [$"ConnectionStrings:{dialect}"] = TestConnections.For(dialect) });

        var (isError, json, text) = await mcp.CallAsync("ping", new Dictionary<string, object?>());

        isError.Should().BeFalse(text);
        var target = json.GetProperty("target");
        target.GetProperty("dialect").GetString().Should().Be(dialect.ToString());
        target.GetProperty("database").GetString().Should().Be("dblens_demo");
        target.GetProperty("user").GetString().Should().Be("dblens_reader");
        target.GetProperty("isLocal").GetBoolean().Should().BeTrue();
        target.GetProperty("source").GetString().Should().Be("Configured");
        json.GetProperty("databaseServerVersion").GetString().Should().Contain(dialect == DatabaseDialect.PostgreSql ? "PostgreSQL" : "SQL Server");
        text.Should().NotContainEquivalentOf("password");
    }

    [Theory]
    [InlineData(DatabaseDialect.PostgreSql, "Host=127.0.0.1;Port=1;Database=nowhere;Username=u;Password=s3cret;Timeout=2")]
    [InlineData(DatabaseDialect.SqlServer, "Server=127.0.0.1,1;Database=nowhere;User Id=u;Password=s3cret;Connect Timeout=2;TrustServerCertificate=true")]
    public async Task Ping_OnAServerThatDoesNotAnswer_IsAnError_NotReachable(DatabaseDialect dialect, string cs)
    {
        await using var mcp = await McpHarness.StartAsync(new Dictionary<string, string?>());

        var (isError, _, text) = await mcp.CallAsync("ping", new Dictionary<string, object?> { ["dialect"] = dialect.ToString(), ["connectionString"] = cs });

        isError.Should().BeTrue();
        text.Should().NotContain("s3cret");
    }

    [SkippableTheory]
    [InlineData(DatabaseDialect.PostgreSql, "public")]
    [InlineData(DatabaseDialect.SqlServer, "dbo")]
    [Trait("Category", "Integration")]
    public async Task EveryKindOfResult_CarriesTheTarget(DatabaseDialect dialect, string schema)
    {
        Skip.If(!await TestConnections.IsReachableAsync(dialect), TestSkip.Reason);
        await using var mcp = await McpHarness.StartAsync(new Dictionary<string, string?> { ["DbLens:DemoFallback"] = "true" });
        var common = new Dictionary<string, object?> { ["dialect"] = dialect.ToString(), ["connectionString"] = TestConnections.For(dialect) };

        foreach (var (tool, arguments) in new (string, Dictionary<string, object?>)[]
                 {
                     ("get_schema", new() { ["schemaFilter"] = schema }),
                     ("get_table_details", new() { ["tables"] = new[] { "users" }, ["schema"] = schema }),
                     ("analyze_indexes", new() { ["schema"] = schema }),
                     ("check_migration_safety", new() { ["ddl"] = "ALTER TABLE users ADD note varchar(10) NULL" }),
                     ("explain_query", new() { ["sql"] = "SELECT id FROM users" }),
                 })
        {
            var (isError, json, text) = await mcp.CallAsync(tool, common.Concat(arguments).ToDictionary());

            isError.Should().BeFalse($"{tool}: {text}");
            json.GetProperty("target").GetProperty("source").GetString().Should().Be("Parameter", tool);
            text.Should().NotContain("readerpassword", tool).And.NotContain("ReaderPassword123!", tool);
        }
    }
}
