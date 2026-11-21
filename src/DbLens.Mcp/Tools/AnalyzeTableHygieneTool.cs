using System.ComponentModel;
using DbLens.Core.Analysis.EfCore;
using DbLens.Core.Analysis.Hygiene;
using DbLens.Core.Models;
using DbLens.EfCore;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class AnalyzeTableHygieneTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "Finds columns nobody fills (always NULL, one value, abandoned by recent rows, named as left behind, not in the " +
        "EF model) and creation or update dates that are missing, nullable, without time zone or not kept up to date, " +
        "each with the EF Core change in Metadata[\"ef\"]. IMPORTANT: signals come from planner statistics and names; " +
        "verifySignals confirms them with two counts per check, never a value. The snapshot is parsed as text. Do NOT " +
        "infer beyond the response.")]
    public Task<AnalyzeTableHygieneResult> AnalyzeTableHygiene(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(SchemaHelp)]
        string? schema = null,
        [Description(TablesHelp)]
        string[]? tables = null,
        [Description("Confirm signals with read-only aggregate queries: two counts each, no values.")]
        bool verifySignals = false,
        [Description("Absolute path of a *ModelSnapshot.cs, or of a folder with one per DbContext: maps findings to entities.")]
        string? modelSnapshotPath = null,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        [Description(ConnectionHelp)]
        string? connection = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<EfModel> models = [];
        string? modelCaveat = null;

        // An unreadable snapshot costs the model, not the analysis.
        if (!string.IsNullOrWhiteSpace(modelSnapshotPath))
        {
            try { models = SnapshotReader.ReadAll(modelSnapshotPath); }
            catch (ArgumentException ex) { modelCaveat = $"The EF model was not read, so findings name no entity: {ex.Message}"; }
        }

        return ResolveInspector(dialect, connectionString, connection).AnalyzeTableHygieneAsync(
            new AnalyzeTableHygieneRequest(schema, tables, verifySignals, models, modelCaveat), cancellationToken);
    }
}
