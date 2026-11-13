namespace DbLens.Tests;

/// <summary>
/// Why an integration test did not run.
///
/// These tests used to return early when the database was unreachable, which xUnit reported as
/// PASSED — a green run could not be told apart from a run that executed nothing. They now raise
/// a real skip, so an unreachable database shows up in the run summary as Skipped.
/// </summary>
public static class TestSkip
{
    public const string Reason =
        "Database not reachable — start the demo containers with 'make db' (docker compose up -d). " +
        "This test did NOT run; it is skipped, not passed.";
}
