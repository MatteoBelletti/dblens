using DbLens.Core.Inspection;
using FluentAssertions;

namespace DbLens.Tests;

public class LocalServerTests
{
    [Theory]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("/var/run/postgresql")]
    [InlineData("localhost,1434")]
    [InlineData("tcp:localhost,1434")]
    [InlineData("127.0.0.1:5433")]
    [InlineData(".")]
    [InlineData(@".\SQLEXPRESS")]
    [InlineData("(local)")]
    [InlineData(@"(localdb)\MSSQLLocalDB")]
    [InlineData(@"np:\\localhost\pipe\sql\query")]
    [InlineData(@"np:\\.\pipe\sql\query")]
    [InlineData("db.localhost")]
    public void ThisMachine_IsLocal(string host) => LocalServer.IsLocal(host).Should().BeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("db.internal.example.com")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.20,1433")]
    [InlineData(@"sqlprod01\ERP")]
    [InlineData(@"np:\\sqlprod01\pipe\sql\query")]
    [InlineData("tcp:erp.database.windows.net,1433")]
    [InlineData("localhost.example.com")]
    public void AnotherMachine_IsNotLocal(string host) => LocalServer.IsLocal(host).Should().BeFalse();

    [Fact]
    public void MachineName_IsLocal() => LocalServer.IsLocal(Environment.MachineName).Should().BeTrue();
}
