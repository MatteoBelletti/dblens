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
        "unknown to the code, migrations pending, tables, columns, indexes and foreign keys the model expects and the " +
        "database lacks, and indexes or tables the database has and the model does not. Each finding names the entity " +
        "and property to change in Metadata[\"ef\"]; Migrations gives the command that scripts the pending ones. " +
        "IMPORTANT: the snapshot is parsed as text — no project is built and no code of the user's runs. Column types, " +
        "lengths and nullability are not compared. Read the Caveats: mappings dblens does not compare are named there. " +
        "Do NOT infer differences beyond what this tool returns.")]
    public Task<CompareEfModelResult> CompareEfModel(
        [Description("Absolute path of the *ModelSnapshot.cs file, or of the Migrations folder that holds it.")]
        string snapshotPath,
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("Schema to compare. Null = every schema the model maps.")]
        string? schema = null,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        CancellationToken cancellationToken = default) =>
        ResolveInspector(dialect, connectionString).CompareEfModelAsync(
            new CompareEfModelRequest(SnapshotReader.Read(snapshotPath), schema), cancellationToken);
}
