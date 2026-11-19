using System.Text.Json;
using System.Text.Json.Nodes;
using DbLens.Mcp.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DbLens.Tests;

/// <summary>
/// The tools and their input schemas, against a versioned snapshot: every client (Claude Desktop, the
/// plugin, any MCP client) depends on them. A tool or parameter may be added; one removed, renamed,
/// retyped or newly required breaks callers and fails here until it is decided. Descriptions are not
/// compared: their words may change. DBLENS_UPDATE_TOOL_SURFACE=1 rewrites the snapshot after an addition.
/// </summary>
public sealed class ToolSurfaceSnapshotTests
{
    private const string FileName = "tool-surface.json";

    private static JsonObject Current()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithToolsFromAssembly(typeof(PingTool).Assembly);
        using var provider = services.BuildServiceProvider();

        JsonObject surface = [];
        foreach (var tool in provider.GetServices<McpServerTool>().OrderBy(t => t.ProtocolTool.Name, StringComparer.Ordinal))
            surface[tool.ProtocolTool.Name] = WithoutDescriptions(JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!);
        return surface;
    }

    private static JsonNode WithoutDescriptions(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("description");
            foreach (var (_, child) in obj.ToList())
                if (child is not null) WithoutDescriptions(child);
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.OfType<JsonNode>()) WithoutDescriptions(child);
        }

        return node;
    }

    private static JsonObject Snapshot() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", FileName)))!.AsObject();

    [Fact]
    public void NoToolOrParameter_IsRemovedChangedOrNewlyRequired()
    {
        var current = Current();
        List<string> breaks = [];

        foreach (var (name, expected) in Snapshot())
        {
            if (current[name] is not JsonObject actual)
            {
                breaks.Add($"{name}: removed");
                continue;
            }

            var expectedProperties = expected!["properties"]?.AsObject() ?? [];
            var actualProperties = actual["properties"]?.AsObject() ?? [];
            foreach (var (parameter, schema) in expectedProperties)
                if (!JsonNode.DeepEquals(schema, actualProperties[parameter]))
                    breaks.Add($"{name}.{parameter}: {(actualProperties[parameter] is null ? "removed" : "changed")}");

            var required = Names(expected["required"]);
            breaks.AddRange(Names(actual["required"]).Where(r => !required.Contains(r)).Select(r => $"{name}.{r}: newly required"));
        }

        breaks.Should().BeEmpty("a client built against the published tools would break; that takes an explicit decision");
    }

    [Fact]
    public void TheSnapshot_IsTheCurrentSurface()
    {
        var current = Current();
        if (Environment.GetEnvironmentVariable("DBLENS_UPDATE_TOOL_SURFACE") == "1")
        {
            File.WriteAllText(SourcePath(), current.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return;
        }

        JsonNode.DeepEquals(current, Snapshot()).Should().BeTrue(
            $"an added tool or parameter is recorded in Fixtures/{FileName}: run the tests once with DBLENS_UPDATE_TOOL_SURFACE=1 and commit it");
    }

    private static HashSet<string> Names(JsonNode? array) =>
        [.. (array as JsonArray ?? []).Select(n => n!.GetValue<string>())];

    /// <summary>The snapshot in the source tree, found from the test's output folder.</summary>
    private static string SourcePath()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "DbLens.Tests.csproj"))) folder = folder.Parent;
        return Path.Combine(folder?.FullName ?? throw new DirectoryNotFoundException("DbLens.Tests.csproj"), "Fixtures", FileName);
    }
}
