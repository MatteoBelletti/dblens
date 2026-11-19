using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;

namespace DbLens.Tests;

/// <summary>
/// One version everywhere a release reads it: the plugin is installed from the repository and starts
/// the server pinned to its own version, so a plugin ahead of or behind the package would run tools its
/// agents do not expect.
/// </summary>
public sealed class ReleaseVersionTests
{
    private static string Root()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "dblens.slnx"))) folder = folder.Parent;
        return folder?.FullName ?? throw new DirectoryNotFoundException("dblens.slnx");
    }

    private static JsonNode Json(params string[] path) => JsonNode.Parse(File.ReadAllText(Path.Combine([Root(), .. path])))!;

    [Fact]
    public void ThePackage_ServerJson_AndThePlugin_ShareOneVersion()
    {
        var version = XDocument.Load(Path.Combine(Root(), "src", "DbLens.Mcp", "DbLens.Mcp.csproj"))
            .Descendants("Version").Single().Value;
        var server = Json("src", "DbLens.Mcp", ".mcp", "server.json");
        var args = Json("plugins", "dblens", ".mcp.json")["mcpServers"]!["dblens"]!["args"]!.AsArray().Select(a => a!.GetValue<string>());

        server["version"]!.GetValue<string>().Should().Be(version);
        server["packages"]![0]!["version"]!.GetValue<string>().Should().Be(version);
        Json("plugins", "dblens", ".claude-plugin", "plugin.json")["version"]!.GetValue<string>().Should().Be(version);
        args.Should().Contain($"DbLens.Mcp@{version}", "the plugin starts the package of its own version");
    }
}
