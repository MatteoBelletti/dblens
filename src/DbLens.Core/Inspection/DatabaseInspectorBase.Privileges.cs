using System.Data.Common;

namespace DbLens.Core.Inspection;

/// <summary>
/// The last line behind the statement guard and the rolled-back transaction: caller SQL is only
/// executed on a login that cannot write. A guard is a filter and can miss something; a login
/// without write permissions cannot be talked into writing.
/// </summary>
public abstract partial class DatabaseInspectorBase
{
    /// <summary>
    /// Lets caller SQL run on a login that can write. For a disposable local database only; the
    /// server exposes it as <c>DbLens:AllowWriteCapableLogin</c>.
    /// </summary>
    public bool AllowWriteCapableLogin { get; init; }

    /// <summary>
    /// Why the connected login could change data (superuser, table write privileges, EXECUTE…),
    /// or null when it is read-only.
    /// </summary>
    protected abstract Task<string?> DescribeWriteCapabilityAsync(DbConnection conn, CancellationToken ct);

    /// <summary>Throws unless caller SQL may be executed on this login.</summary>
    protected async Task EnsureReadOnlyLoginAsync(CancellationToken ct)
    {
        if (AllowWriteCapableLogin) return;

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        if (await DescribeWriteCapabilityAsync(conn, ct) is { } why)
            throw new Models.ExecutionRefusedException(
                $"Refusing to execute caller SQL: the login can write ({why}). Connect dblens with a read-only " +
                "account — see docs/safety.md#read-only-login. Estimated plans (analyze = false) need no execution " +
                "and still work.");
    }
}
