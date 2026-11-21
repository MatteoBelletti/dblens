namespace DbLens.Core.Models;

/// <summary>Where a call's database came from.</summary>
public enum TargetSource
{
    /// <summary><c>ConnectionStrings:&lt;dialect&gt;</c> in the server's configuration.</summary>
    Configured,

    /// <summary>The caller's <c>connectionString</c> parameter.</summary>
    Parameter,

    /// <summary>The local demo database: nothing configured, and <c>DbLens:DemoFallback</c> on.</summary>
    DemoDefault,
}

/// <summary>
/// The database a call ran against, read from the connection string with the driver's own builder:
/// never its password. <see cref="IsLocal"/> is decided from the address alone (see LocalServer).
/// </summary>
public sealed record Target(
    DatabaseDialect Dialect,
    string? Host,
    int? Port,
    string? Database,
    string? User,
    bool IsLocal,
    TargetSource Source);
