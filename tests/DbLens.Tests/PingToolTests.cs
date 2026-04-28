using DbLens.Core.Models;
using DbLens.Mcp.Tools;
using FluentAssertions;

namespace DbLens.Tests;

public sealed class PingToolTests
{
    [Fact]
    public void Ping_DefaultMessage_ReturnsPong()
    {
        var result = PingTool.Ping();

        result.Message.Should().Be("pong");
    }

    [Fact]
    public void Ping_CustomMessage_ReflectsInput()
    {
        var result = PingTool.Ping("hello");

        result.Message.Should().Be("hello");
    }

    [Fact]
    public void Ping_AvailableDialects_ContainsBothDialects()
    {
        var result = PingTool.Ping();

        result.AvailableDialects.Should().Contain(nameof(DatabaseDialect.PostgreSql));
        result.AvailableDialects.Should().Contain(nameof(DatabaseDialect.SqlServer));
    }

    [Fact]
    public void Ping_Timestamp_IsUtcIso8601()
    {
        var before = DateTime.UtcNow;
        var result = PingTool.Ping();
        var after = DateTime.UtcNow;

        var timestamp = DateTime.Parse(result.Timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind);
        timestamp.Kind.Should().Be(DateTimeKind.Utc);
        timestamp.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Ping_ServerVersion_IsNotEmpty()
    {
        var result = PingTool.Ping();

        result.ServerVersion.Should().NotBeNullOrWhiteSpace();
    }
}
