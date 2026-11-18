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
        "EF model) and tables whose creation and update dates are missing, nullable, without time zone or not kept up " +
        "to date. Each finding carries the EF Core change in Metadata[\"ef\"]. " +
        "IMPORTANT: signals come from planner statistics and names; verifySignals counts rows to confirm them — two " +
        "counts per check, never a value, not even a constant column's. The snapshot, when given, is parsed as text. " +
        "Do NOT infer findings beyond what this tool returns.")]
    public Task<AnalyzeTableHygieneResult> AnalyzeTableHygiene(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(SchemaHelp)]
        string? schema = null,
        [Description(TablesHelp)]
        string[]? tables = null,
        [Description("Confirm signals with read-only aggregate queries: two counts each, no values. Default false.")]
        bool verifySignals = false,
        [Description("Absolute path of a *ModelSnapshot.cs or of a folder holding one per DbContext. Optional: maps findings to entities.")]
        string? modelSnapshotPath = null,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
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

        return ResolveInspector(dialect, connectionString).AnalyzeTableHygieneAsync(
            new AnalyzeTableHygieneRequest(schema, tables, verifySignals, models, modelCaveat), cancellationToken);
    }
}
