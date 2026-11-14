using Microsoft.Extensions.Configuration;

namespace DbLens.Mcp.Infrastructure;

/// <summary>
/// Operator decisions that change the product's contract: server configuration, never a tool
/// parameter a model could set. Each is described in docs/safety.md.
/// </summary>
public sealed record ServerPolicy(bool AllowQueryResults, bool AllowWriteCapableLogin, bool ReadOnlyLoginOnLocalhost)
{
    public static ServerPolicy From(IConfiguration configuration) => new(
        configuration.GetValue("DbLens:AllowQueryResults", false),
        configuration.GetValue("DbLens:AllowWriteCapableLogin", false),
        configuration.GetValue("DbLens:ReadOnlyLoginOnLocalhost", false));
}
