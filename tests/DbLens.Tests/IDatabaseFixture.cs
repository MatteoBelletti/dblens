using DbLens.Core.Abstractions;

namespace DbLens.Tests;

/// <summary>
/// What a dialect-neutral integration test needs from a database fixture. Lets one abstract test
/// class run unchanged against every dialect, so the two cannot quietly drift apart.
/// </summary>
public interface IDatabaseFixture
{
    bool IsReachable { get; }

    IDatabaseInspector? Inspector { get; }

    /// <summary>The demo schema holding the deliberately flawed tables: "public" or "dbo".</summary>
    string UserSchema { get; }

    /// <summary>An inspector on the administrator login, which can write.</summary>
    IDatabaseInspector CreateAdminInspector(bool readOnlyLoginOnLocalhost = false);
}
