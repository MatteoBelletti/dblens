using Microsoft.Extensions.Configuration;

namespace DbLens.Mcp.Infrastructure;

/// <summary>
/// Operator decisions that change the product's contract, so they are server configuration and
/// never tool parameters a model could set.
/// </summary>
public sealed record ServerPolicy(bool AllowQueryResults, bool AllowWriteCapableLogin)
{
    /// <summary>
    /// <c>DbLens:AllowQueryResults</c> — enables query_with_receipt, the one tool that returns values
    /// from the data. Off by default: dblens otherwise never returns row data.
    /// <c>DbLens:AllowWriteCapableLogin</c> — lets caller SQL run on a login that can write. For a
    /// disposable local database only.
    /// </summary>
    public static ServerPolicy From(IConfiguration configuration) => new(
        configuration.GetValue("DbLens:AllowQueryResults", false),
        configuration.GetValue("DbLens:AllowWriteCapableLogin", false));
}
