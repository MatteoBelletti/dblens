using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DbLens.Tests;

/// <summary>A connection saved under a name reaches its database while only the name travels through a call.</summary>
public sealed class NamedConnectionsTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("dblens-connections-");

    public void Dispose() => _folder.Delete(recursive: true);

    private NamedConnections Store() =>
        new(new ConfigurationBuilder().AddInMemoryCollection([new("DbLens:ConnectionsPath", _folder.FullName)]).Build());

    private void Save(string name, DatabaseDialect dialect, string connectionString) =>
        File.WriteAllLines(Path.Combine(_folder.FullName, name + ".conn"), [dialect.ToString(), connectionString]);

    [Fact]
    public void ASavedConnection_CarriesItsDialect()
    {
        Save("local-ss", DatabaseDialect.SqlServer, "Server=localhost,1500;Database=x");

        Store().Resolve("local-ss").Should().Be(
            new ConfiguredDatabase(DatabaseDialect.SqlServer, "Server=localhost,1500;Database=x", TargetSource.Named));
    }

    [Theory]
    [InlineData("../etc/passwd", "is not a connection name")]
    [InlineData("missing", "No connection named 'missing'")]
    public void ABadOrMissingName_IsAnError(string name, string message) =>
        Store().Invoking(s => s.Resolve(name)).Should().Throw<ArgumentException>().WithMessage($"*{message}*");

    [Fact]
    public void AMalformedFile_IsAnError()
    {
        File.WriteAllText(Path.Combine(_folder.FullName, "broken.conn"), "Oracle\n");

        Store().Invoking(s => s.Resolve("broken")).Should().Throw<ArgumentException>().WithMessage("*not a dialect line*");
    }

    [SkippableTheory]
    [InlineData(DatabaseDialect.PostgreSql, "public")]
    [InlineData(DatabaseDialect.SqlServer, "dbo")]
    [Trait("Category", "Integration")]
    public async Task OverMcp_OnlyTheNameTravels_AndTheTargetNamesIt(DatabaseDialect dialect, string schema)
    {
        Skip.If(!await TestConnections.IsReachableAsync(dialect), TestSkip.Reason);
        Save("chosen", dialect, TestConnections.For(dialect));
        await using var mcp = await McpHarness.StartAsync(new Dictionary<string, string?> { ["DbLens:ConnectionsPath"] = _folder.FullName });

        var (isError, json, text) = await mcp.CallAsync("get_schema", new Dictionary<string, object?> { ["connection"] = "chosen", ["schemaFilter"] = schema });

        isError.Should().BeFalse(text);
        json.GetProperty("dialect").GetString().Should().Be(dialect.ToString());
        var target = json.GetProperty("target");
        target.GetProperty("source").GetString().Should().Be("Named");
        target.GetProperty("connection").GetString().Should().Be("chosen");
        text.Should().NotContain("readerpassword").And.NotContain("ReaderPassword123!");
    }

    [Fact]
    public async Task OverMcp_ConnectionAndConnectionStringTogether_IsAnError()
    {
        Save("chosen", DatabaseDialect.PostgreSql, "Host=localhost;Database=x");
        await using var mcp = await McpHarness.StartAsync(new Dictionary<string, string?> { ["DbLens:ConnectionsPath"] = _folder.FullName });

        var (isError, _, text) = await mcp.CallAsync("get_schema", new Dictionary<string, object?>
        {
            ["connection"] = "chosen", ["dialect"] = "PostgreSql", ["connectionString"] = "Host=other",
        });

        isError.Should().BeTrue();
        text.Should().Be("Pass connection or connectionString, not both.");
    }

    [Fact]
    public async Task OverMcp_ADialectThatContradictsTheConnection_IsAnError()
    {
        Save("chosen", DatabaseDialect.SqlServer, "Server=localhost;Database=x");
        await using var mcp = await McpHarness.StartAsync(new Dictionary<string, string?> { ["DbLens:ConnectionsPath"] = _folder.FullName });

        var (isError, _, text) = await mcp.CallAsync("get_schema", new Dictionary<string, object?> { ["connection"] = "chosen", ["dialect"] = "PostgreSql" });

        isError.Should().BeTrue();
        text.Should().StartWith("The connection 'chosen' is SqlServer, not PostgreSql");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task OverMcp_ACertifiedQuery_IsStoredUnderTheResolvedDialect_NotTheSchemaDefault()
    {
        Skip.If(!await TestConnections.IsReachableAsync(DatabaseDialect.SqlServer), TestSkip.Reason);
        await using var mcp = await McpHarness.StartAsync(new Dictionary<string, string?> { ["ConnectionStrings:SqlServer"] = TestConnections.SqlServer });

        var (isError, json, text) = await mcp.CallAsync("certify_query", new Dictionary<string, object?>
        {
            ["name"] = "users", ["question"] = "How many users?", ["sql"] = "SELECT COUNT_BIG(*) AS n FROM dbo.users",
        });

        isError.Should().BeFalse(text);
        json.GetProperty("certified").GetProperty("dialect").GetString().Should().Be("SqlServer");
    }
}
