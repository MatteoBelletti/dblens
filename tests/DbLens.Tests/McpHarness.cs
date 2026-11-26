using System.IO.Pipelines;
using System.Text.Json;
using DbLens.Mcp.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DbLens.Tests;

/// <summary>
/// The real server (<see cref="DbLensServer.AddDbLensServer"/>) and an MCP client, joined by pipes:
/// a call goes through argument binding, the call filter and the tool as a client's would.
/// </summary>
internal sealed class McpHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    private readonly string _certified = Path.Combine(Path.GetTempPath(), $"dblens-certified-{Guid.NewGuid():N}.json");

    public McpClient Client { get; private set; } = null!;

    private McpHarness(IReadOnlyDictionary<string, string?> settings, Pipe toServer, Pipe toClient)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Append(new("DbLens:CertifiedQueriesPath", _certified)))
            .Build();
        var services = new ServiceCollection().AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance).AddLogging();
        services.AddDbLensServer(configuration).WithStreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream());
        _provider = services.BuildServiceProvider();
        _run = _provider.GetRequiredService<McpServer>().RunAsync(_stop.Token);
    }

    public static async Task<McpHarness> StartAsync(IReadOnlyDictionary<string, string?> settings)
    {
        Pipe toServer = new(), toClient = new();
        var harness = new McpHarness(settings, toServer, toClient);
        harness.Client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream(), NullLoggerFactory.Instance),
            loggerFactory: NullLoggerFactory.Instance);
        return harness;
    }

    /// <summary>The tool's JSON answer, or the error text when it failed.</summary>
    public async Task<(bool IsError, JsonElement Json, string Text)> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments)
    {
        var result = await Client.CallToolAsync(tool, arguments);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        return (result.IsError == true, result.IsError == true ? default : JsonDocument.Parse(text).RootElement.Clone(), text);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _stop.CancelAsync();
        try { await _run; } catch (OperationCanceledException) { }
        await _provider.DisposeAsync();
        File.Delete(_certified);
    }
}
