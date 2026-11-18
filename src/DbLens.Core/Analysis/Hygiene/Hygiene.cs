using System.Text.Json;
using DbLens.Core.Analysis.EfCore;
using DbLens.Core.Analysis.Verification;

namespace DbLens.Core.Analysis.Hygiene;

public sealed record AnalyzeTableHygieneRequest(
    string? Schema = null,
    string[]? Tables = null,
    bool VerifySignals = false,
    /// <summary>The EF Core models of the database, read from their snapshots. Empty = no model.</summary>
    IReadOnlyList<EfModel>? Models = null,
    /// <summary>Why the snapshot could not be read, when one was given. Reported, the rest still runs.</summary>
    string? ModelCaveat = null,
    long MinTableRows = 1_000,
    long MinSingleValueRows = 100,
    long MinRecentRows = 1_000,
    int RecentDays = 90,
    double UnmaintainedShare = 0.95,
    /// <summary>Days the engine's update counters must cover before a zero means "never updated".</summary>
    int UpdateCountersMinDays = 7,
    int MaxVerificationQueries = 20,
    int VerificationTimeoutSeconds = 10,
    int VerificationBudgetSeconds = 30,
    long MaxVerifiableTableRows = 5_000_000)
{
    public VerificationBudget Budget =>
        new(MaxVerificationQueries, VerificationTimeoutSeconds, VerificationBudgetSeconds, MaxVerifiableTableRows);
}

public sealed record AnalyzeTableHygieneResult(
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<Finding> Signals,
    HygieneSummary Summary,
    IReadOnlyList<string> Caveats,
    bool Truncated,
    /// <summary>Null when verification was not requested.</summary>
    VerificationSummary? Verification = null);

public sealed record HygieneSummary(
    int TotalFindings,
    int TotalSignals,
    int TablesAnalyzed,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByRule);

/// <summary>
/// What the planner's statistics say of a column, never what it holds: whether every sampled row is
/// NULL, and whether the filled rows hold one distinct value. Null where the engine keeps no statistics.
/// </summary>
public sealed record ColumnStatistics(string Schema, string Table, string Column, bool AllNull, bool SingleValue);

/// <summary>Updates the engine counted on a table since its counters started.</summary>
public sealed record TableUpdates(string Schema, string Table, long Updates);

/// <summary>Creation and update column names, and the words that mark a column as left behind.</summary>
public sealed class AuditVocabulary
{
    public static readonly AuditVocabulary Default = Load();

    public required IReadOnlyList<string> Created { get; init; }
    public required IReadOnlyList<string> Updated { get; init; }
    public required IReadOnlySet<string> LegacyTokens { get; init; }

    private static AuditVocabulary Load()
    {
        const string resource = "DbLens.Core.Analysis.Hygiene.audit_columns.json";
        using var stream = typeof(AuditVocabulary).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource '{resource}' not found.");
        var root = JsonDocument.Parse(stream).RootElement;

        static List<string> Words(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetString()!)];

        return new AuditVocabulary
        {
            Created = Words(root.GetProperty("created")),
            Updated = Words(root.GetProperty("updated")),
            LegacyTokens = Words(root.GetProperty("legacyTokens")).ToHashSet(StringComparer.Ordinal),
        };
    }
}
