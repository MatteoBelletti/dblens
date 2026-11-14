using System.Data.Common;

namespace DbLens.Core.Inspection;

/// <summary>
/// The layer behind the statement guard and the rolled-back transaction: caller SQL runs only on a
/// login that cannot write. A filter can miss something; a login without write permissions cannot
/// be talked into writing. A database on this machine is exempt: there the admin login is the
/// normal one.
/// </summary>
public abstract partial class DatabaseInspectorBase
{
    /// <summary>
    /// Lets caller SQL run on a login that can write, wherever the server is. For a disposable
    /// database only; the server exposes it as <c>DbLens:AllowWriteCapableLogin</c>.
    /// </summary>
    public bool AllowWriteCapableLogin { get; init; }

    /// <summary>
    /// Applies the read-only-login check to a server on this machine too — for dblens running on a
    /// real database server. Exposed as <c>DbLens:ReadOnlyLoginOnLocalhost</c>.
    /// </summary>
    public bool ReadOnlyLoginOnLocalhost { get; init; }

    /// <summary>The server addresses the connection string names.</summary>
    protected abstract IReadOnlyList<string> ServerHosts { get; }

    /// <summary>
    /// Why the connected login could change data (superuser, table write privileges, EXECUTE…),
    /// or null when it is read-only.
    /// </summary>
    protected abstract Task<string?> DescribeWriteCapabilityAsync(DbConnection conn, CancellationToken ct);

    /// <summary>Whether caller SQL on this inspector is checked against the login's privileges.</summary>
    public bool RequiresReadOnlyLogin =>
        !AllowWriteCapableLogin
        && (ReadOnlyLoginOnLocalhost || ServerHosts.Count == 0 || !ServerHosts.All(LocalServer.IsLocal));

    /// <summary>Throws unless caller SQL may be executed on this login.</summary>
    protected async Task EnsureReadOnlyLoginAsync(CancellationToken ct)
    {
        if (!RequiresReadOnlyLogin) return;

        await using var conn = await OpenAsync(ct);

        if (await DescribeWriteCapabilityAsync(conn, ct) is { } why)
            throw new Models.ExecutionRefusedException(
                $"Refusing to execute caller SQL: the login can write ({why}) and the server is not on this " +
                "machine. Connect dblens with a read-only account — see docs/safety.md#read-only-login. " +
                "Estimated plans (analyze = false) need no execution and still work.");
    }
}
