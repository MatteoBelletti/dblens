using DbLens.Core.Models;
using DbLens.Mcp.Tools;
using FluentAssertions;

namespace DbLens.Tests;

public sealed class PingToolTests
{
    private static PingTool Tool() => new(new StubServices(() => new StubInspector()), new StubFactory(() => new StubInspector()));

    [Fact]
    public async Task Ping_EchoesTheMessage_AndReportsTheDatabaseVersion()
    {
        var result = await Tool().Ping("hello");

        result.Message.Should().Be("hello");
        result.DatabaseServerVersion.Should().Be("stub 1.0");
        result.AvailableDialects.Should().Equal(nameof(DatabaseDialect.PostgreSql), nameof(DatabaseDialect.SqlServer));
        result.ServerVersion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Ping_Timestamp_IsUtcIso8601()
    {
        var before = DateTime.UtcNow;
        var result = await Tool().Ping();
        var after = DateTime.UtcNow;

        var timestamp = DateTime.Parse(result.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind);
        timestamp.Kind.Should().Be(DateTimeKind.Utc);
        timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}
