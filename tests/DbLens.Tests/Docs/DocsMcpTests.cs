using System.IO.Pipelines;
using System.Text.Json;
using DbLens.Docs.Documents;
using DbLens.Docs.Mcp;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DbLens.Tests.Docs;

/// <summary>The real server and a client over pipes, on a folder holding a small solution.</summary>
public sealed class DocsMcpTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dblens-docs-mcp-{Guid.NewGuid():N}");
    private readonly CancellationTokenSource _stop = new();
    private ServiceProvider _provider = null!;
    private McpClient _client = null!;
    private Task _run = Task.CompletedTask;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src/Api"));
        await File.WriteAllTextAsync(Path.Combine(_root, "src/Api/Api.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(_root, "src/Api/Api.cs"), "public static class A { public static void M(WebApplication app) { app.MapGet(\"/a\", H); } }");
        await File.WriteAllTextAsync(Path.Combine(_root, "secret.txt"), "not a project file");

        Pipe toServer = new(), toClient = new();
        var services = new ServiceCollection().AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance).AddLogging();
        services.AddDocsServer(_root).WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream());
        _provider = services.BuildServiceProvider();
        _run = _provider.GetRequiredService<McpServer>().RunAsync(_stop.Token);
        _client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream(), NullLoggerFactory.Instance), loggerFactory: NullLoggerFactory.Instance);
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _stop.CancelAsync();
        try { await _run; } catch (OperationCanceledException) { }
        await _provider.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }

    private async Task<(bool IsError, string Text)> Call(string tool, params (string Key, object? Value)[] arguments)
    {
        var result = await _client.CallToolAsync(tool, arguments.ToDictionary(a => a.Key, a => a.Value));
        return (result.IsError == true, string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
    }

    private void Scaffold()
    {
        var workspace = new DocsWorkspace(_root);
        DocsFolder.Write(_root, workspace.Options, Scaffolder.Scaffold(workspace.Code().Inventory, new Dictionary<string, string>(), workspace.Options).Files);
    }

    [Fact]
    public async Task TheToolsAreTheFourOfTheSurface_AndNoneTakesAPath()
    {
        var tools = await _client.ListToolsAsync();

        tools.Select(t => t.Name).Should().BeEquivalentTo("get_project_inventory", "get_doc_slots", "check_doc_coverage", "set_doc_slot");
        tools.SelectMany(t => JsonDocument.Parse(t.ProtocolTool.InputSchema.GetRawText()).RootElement.GetProperty("properties").EnumerateObject())
            .Select(p => p.Name).Should().NotContain(["path", "root", "file", "folder", "directory"]);
    }

    [Fact]
    public async Task TheSurface_IsPinned_ARemovedOrRenamedParameterBreaksThePluginAndFailsHere()
    {
        var tools = await _client.ListToolsAsync();

        string[] Parameters(string tool) =>
            [.. JsonDocument.Parse(tools.Single(t => t.Name == tool).ProtocolTool.InputSchema.GetRawText()).RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
        Parameters("get_project_inventory").Should().Equal("idPrefix", "kind");
        Parameters("get_doc_slots").Should().Equal("brief", "idPrefix", "ids", "state");
        Parameters("check_doc_coverage").Should().Equal("requireSlots", "strict");
        Parameters("set_doc_slot").Should().Equal("id", "text");
        tools.Where(t => t.Name != "set_doc_slot").Should().OnlyContain(t => t.ProtocolTool.Annotations!.ReadOnlyHint == true, "only set_doc_slot writes");
    }

    [Fact]
    public async Task TheInventory_IsFilteredByKindAndPrefix()
    {
        var all = JsonDocument.Parse((await Call("get_project_inventory")).Text).RootElement;
        var endpoints = JsonDocument.Parse((await Call("get_project_inventory", ("kind", "endpoint"))).Text).RootElement;

        all.GetProperty("total").GetInt32().Should().BeGreaterThan(endpoints.GetProperty("total").GetInt32());
        all.TryGetProperty("items", out _).Should().BeFalse("an unfiltered call is the counts only");
        all.GetProperty("byKind").GetProperty("Endpoint").GetInt32().Should().Be(1);
        endpoints.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Should().Equal("endpoint:GET /a");
        (await Call("get_project_inventory", ("kind", "nonsense"))).Should().Match<(bool IsError, string Text)>(r => r.IsError && r.Text.Contains("Unknown kind"));
    }

    [Fact]
    public async Task WithoutDocuments_TheSlotListSaysSo_AndTheCheckReportsEveryItemMissing()
    {
        var slots = JsonDocument.Parse((await Call("get_doc_slots")).Text).RootElement;
        var check = JsonDocument.Parse((await Call("check_doc_coverage")).Text).RootElement;

        slots.GetProperty("total").GetInt32().Should().Be(0);
        slots.GetProperty("note").GetString().Should().Contain("scaffold");
        check.GetProperty("issues").EnumerateArray().Should().Contain(i => i.GetProperty("code").GetString() == "missing-item");
    }

    [Fact]
    public async Task ASlotIsWrittenOnDisk_AndTheCheckPassesWithIt()
    {
        Scaffold();

        var (isError, text) = await Call("set_doc_slot", ("id", "endpoint:GET /a"), ("text", "Lists a."));

        isError.Should().BeFalse(text);
        File.ReadAllText(Path.Combine(_root, "docs/project/api.md")).Should().Contain("Lists a.").And.Contain("evidence=inferred");
        var slots = JsonDocument.Parse((await Call("get_doc_slots", ("state", "current"))).Text).RootElement;
        slots.GetProperty("slots").EnumerateArray().Single().GetProperty("text").GetString().Should().Be("Lists a.");
        JsonDocument.Parse((await Call("check_doc_coverage")).Text).RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task SlotsCanBeAskedForByExactId_Briefly_AndTheCheckGroupsItsIssues()
    {
        File.WriteAllText(Path.Combine(_root, "src/Api/Api.cs"), "public static class A { public static void M(WebApplication app) { app.MapGet(\"/a\", H); app.MapGet(\"/b\", H); app.MapGet(\"/c\", H); } }");
        Scaffold();

        var one = JsonDocument.Parse((await Call("get_doc_slots", ("ids", new[] { "endpoint:GET /b" }))).Text).RootElement;
        var brief = JsonDocument.Parse((await Call("get_doc_slots", ("brief", true))).Text).RootElement;
        var check = JsonDocument.Parse((await Call("check_doc_coverage", ("requireSlots", true))).Text).RootElement;

        one.GetProperty("slots").EnumerateArray().Select(x => x.GetProperty("id").GetString()).Should().Equal("endpoint:GET /b");
        brief.GetProperty("slots").EnumerateArray().Should().OnlyContain(x => x.GetProperty("facts").EnumerateObject().Count() == 0 && x.GetProperty("text").GetString() == "");
        var empty = check.GetProperty("issues").EnumerateArray().Where(i => i.GetProperty("code").GetString() == "slot-empty").ToList();
        empty.Select(i => i.GetProperty("file").GetString()).Should().OnlyHaveUniqueItems("one entry per file");
        empty.Sum(i => i.GetProperty("ids").GetArrayLength()).Should().Be(brief.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task WritesFromManyCallsAtOnce_AreAllKept()
    {
        var routes = Enumerable.Range(0, 12).Select(i => $"/r{i:00}").ToList();
        File.WriteAllText(Path.Combine(_root, "src/Api/Api.cs"), "public static class A { public static void M(WebApplication app) { " + string.Concat(routes.Select((r, i) => $"app.MapGet(\"{r}\", H{i});")) + " } }");
        Scaffold();

        var results = await Task.WhenAll(routes.Select(r => Call("set_doc_slot", ("id", $"endpoint:GET {r}"), ("text", $"Words for {r}."))));

        results.Should().OnlyContain(r => !r.IsError);
        var document = File.ReadAllText(Path.Combine(_root, "docs/project/api.md"));
        routes.Should().OnlyContain(r => document.Contains($"Words for {r}."));
    }

    [Fact]
    public async Task AKindThatIsNotAName_IsRefused()
    {
        foreach (var kind in new[] { "7", "Endpoint,Config", "99" })
            (await Call("get_project_inventory", ("kind", kind))).IsError.Should().BeTrue(kind);
    }

    [Fact]
    public async Task ARefusal_ReachesTheModelAsReadableText_AndWritesNothing()
    {
        Scaffold();
        var before = File.ReadAllText(Path.Combine(_root, "docs/project/api.md"));

        var tooLong = await Call("set_doc_slot", ("id", "endpoint:GET /a"), ("text", "a\nb\nc"));
        var marker = await Call("set_doc_slot", ("id", "endpoint:GET /a"), ("text", "x <!-- dblens:end -->"));
        var unknown = await Call("set_doc_slot", ("id", "endpoint:GET /nowhere"), ("text", "x"));

        tooLong.Should().Match<(bool IsError, string Text)>(r => r.IsError && r.Text.Contains("at most 2"));
        marker.Should().Match<(bool IsError, string Text)>(r => r.IsError && r.Text.Contains("HTML comment"));
        unknown.Should().Match<(bool IsError, string Text)>(r => r.IsError && r.Text.Contains("not in the inventory"));
        File.ReadAllText(Path.Combine(_root, "docs/project/api.md")).Should().Be(before);
    }

    [Fact]
    public async Task APersonsSlot_CannotBeReplacedThroughTheServer()
    {
        Scaffold();
        await Call("set_doc_slot", ("id", "endpoint:GET /a"), ("text", "Lists a."));
        var workspace = new DocsWorkspace(_root);
        var path = Path.Combine(_root, "docs/project/api.md");
        File.WriteAllText(path, Scaffolder.Confirm(File.ReadAllText(path), "endpoint:GET /a", ItemHash.Of(workspace.Code().Inventory.Items.Single(i => i.Id == "endpoint:GET /a"))));

        var refused = await Call("set_doc_slot", ("id", "endpoint:GET /a"), ("text", "Overwritten."));

        refused.Should().Match<(bool IsError, string Text)>(r => r.IsError && r.Text.Contains("person"));
        File.ReadAllText(path).Should().Contain("Lists a.").And.NotContain("Overwritten.");
    }
}
