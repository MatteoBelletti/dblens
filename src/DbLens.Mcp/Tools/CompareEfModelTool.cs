using System.ComponentModel;
using DbLens.Core.Analysis.EfCore;
using DbLens.Core.Models;
using DbLens.EfCore;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class CompareEfModelTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "Compares an EF Core model, read from its migrations snapshot, with the database: migrations applied but " +
        "unknown to the code and pending; tables, columns, indexes and foreign keys the model expects and the database " +
        "lacks, and those the database has and the model does not; column types and nullability, and index uniqueness, " +
        "that differ. Metadata[\"ef\"] names the entity and property to " +
        "change; Migrations gives the command that scripts the pending ones. IMPORTANT: the snapshot is parsed as text, " +
        "no code of the user's runs; the only rows read are the migration ids of EF's history table. Index filters and " +
        "defaults are not compared; mappings not compared are named in the Caveats. Do NOT infer beyond the " +
        "response.")]
    public Task<CompareEfModelResult> CompareEfModel(
        [Description("Absolute path of the *ModelSnapshot.cs file, or of the Migrations folder that holds it.")]
        string snapshotPath,
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("Schema to compare. Null = every schema the model maps.")]
        string? schema = null,
        [Description("The migrations history table, when the DbContext sets MigrationsHistoryTable. Null = " + CompareEfModelRequest.DefaultHistoryTable + ".")]
        string? historyTable = null,
        [Description("Its schema. Null = wherever one is found.")]
        string? historySchema = null,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        [Description(ConnectionHelp)]
        string? connection = null,
        CancellationToken cancellationToken = default) =>
        ResolveInspector(dialect, connectionString, connection).CompareEfModelAsync(
            new CompareEfModelRequest(
                SnapshotReader.Read(snapshotPath), schema,
                string.IsNullOrWhiteSpace(historyTable) ? CompareEfModelRequest.DefaultHistoryTable : historyTable,
                string.IsNullOrWhiteSpace(historySchema) ? null : historySchema),
            cancellationToken);
}
