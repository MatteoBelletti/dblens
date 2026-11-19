namespace DbLens.Core.Analysis.EfCore;

public sealed record CompareEfModelRequest(
    EfModel Model,
    /// <summary>Restrict the comparison to one schema. Null = every schema the model maps.</summary>
    string? Schema = null,
    /// <summary>The migrations history table (MigrationsHistoryTable in the DbContext options).</summary>
    string HistoryTable = CompareEfModelRequest.DefaultHistoryTable,
    /// <summary>Its schema. Null = wherever a table of that name is found.</summary>
    string? HistorySchema = null)
{
    public const string DefaultHistoryTable = "__EFMigrationsHistory";
}

public sealed record CompareEfModelResult(
    /// <summary>Differences between the model and the catalog, all derived from metadata.</summary>
    IReadOnlyList<Finding> Findings,
    /// <summary>Heuristic results. Always empty for this tool; present so every result has one shape.</summary>
    IReadOnlyList<Finding> Signals,
    CompareEfModelSummary Summary,
    EfMigrationState Migrations,
    IReadOnlyList<string> Caveats,
    bool Truncated);

public sealed record CompareEfModelSummary(
    int TotalFindings,
    int TotalSignals,
    int EntitiesCompared,
    int TablesCompared,
    IReadOnlyDictionary<string, int> BySeverity,
    IReadOnlyDictionary<string, int> ByRule);

/// <summary>Where the database stands in the model's migrations.</summary>
public sealed record EfMigrationState(
    /// <summary>Whether a history table was found; without one nothing below was read.</summary>
    bool HistoryFound,
    /// <summary>The history table read, "schema.table".</summary>
    string? HistoryTable,
    /// <summary>The newest migration both the code and the database know. Null = none.</summary>
    string? LastApplied,
    /// <summary>In the code, not applied, oldest first.</summary>
    IReadOnlyList<string> Pending,
    /// <summary>
    /// Pending migrations older than <see cref="LastApplied"/>, usually from a merge: `database update`
    /// applies them, but a script from the last applied one leaves them out.
    /// </summary>
    IReadOnlyList<string> OutOfOrder,
    /// <summary>Applied, not in the code nor in another DbContext of the project: another branch, or a hotfix.</summary>
    IReadOnlyList<string> Unknown,
    /// <summary>Applied migrations that belong to the project's other DbContexts, set aside.</summary>
    IReadOnlyList<string> OtherContexts,
    /// <summary>
    /// The commands that write the SQL of the pending migrations, one per run of consecutive pending
    /// migrations, in order. Empty when none are pending.
    /// </summary>
    IReadOnlyList<string> ScriptCommands);
