using System.ComponentModel;
using System.Reflection;
using DbLens.Core.Models;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class PingTool
{
    [McpServerTool, Description("Verifies the dblens MCP server is reachable and reports basic server info.")]
    public static PingResult Ping(string message = "pong")
    {
        return new PingResult(
            Message: message,
            ServerVersion: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            Timestamp: DateTime.UtcNow.ToString("O"),
            AvailableDialects: [
                nameof(DatabaseDialect.PostgreSql),
                nameof(DatabaseDialect.SqlServer),
            ]);
    }
}

public sealed record PingResult(
    string Message,
    string ServerVersion,
    string Timestamp,
    string[] AvailableDialects);
