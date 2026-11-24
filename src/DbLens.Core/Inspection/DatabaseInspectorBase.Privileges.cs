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

    /// <summary>
    /// Throws unless caller SQL may be executed on this login. When the login can write and only the
    /// server's local address allowed it, returns the caveat that says so: an address says nothing
    /// about what sits behind it.
    /// </summary>
    protected async Task<string?> EnsureReadOnlyLoginAsync(CancellationToken ct)
    {
        if (AllowWriteCapableLogin) return null;

        await using var conn = await OpenAsync(ct);
        var why = await DescribeWriteCapabilityAsync(conn, ct);
        if (why is null) return null;

        if (RequiresReadOnlyLogin)
            throw new Models.ExecutionRefusedException(
                $"Refusing to execute caller SQL: the login can write ({why}) and the server is not on this " +
                "machine. Connect dblens with a read-only account — see docs/safety.md#read-only-login. " +
                "Estimated plans (analyze = false) need no execution and still work.");

        return $"The login can write ({why}) and the server address is local, so the read-only-login check was not " +
               "applied. A tunnel to another machine (ssh -L, kubectl port-forward, a cloud SQL proxy) also " +
               "looks local: unless this is a disposable copy, connect with a read-only login, or ask the " +
               "server's operator to set DbLens:ReadOnlyLoginOnLocalhost=true so that dblens refuses this one." +
               LocalWriteCaveatExtra;
    }

    /// <summary>What a dialect adds about running caller SQL as a login that can write.</summary>
    protected virtual string LocalWriteCaveatExtra => "";
}
