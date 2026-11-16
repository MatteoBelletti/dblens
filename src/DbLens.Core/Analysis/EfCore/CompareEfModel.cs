namespace DbLens.Core.Analysis.EfCore;

public sealed record CompareEfModelRequest(
    EfModel Model,
    /// <summary>Restrict the comparison to one schema. Null = every schema the model maps.</summary>
    string? Schema = null,
    /// <summary>The migrations history table (MigrationsHistoryTable in the DbContext options).</summary>
    string HistoryTable = "__EFMigrationsHistory",
    /// <summary>Its schema. Null = wherever a table of that name is found.</summary>
    string? HistorySchema = null);

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
    /// <summary>Applied, not in the code: another branch, or a hotfix applied by hand.</summary>
    IReadOnlyList<string> Unknown,
    /// <summary>The command that writes the SQL of the pending migrations. Null when none are pending.</summary>
    string? ScriptCommand);
