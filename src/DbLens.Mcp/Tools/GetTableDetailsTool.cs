using System.ComponentModel;
using DbLens.Core.Analysis;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class GetTableDetailsTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "Returns columns, indexes, foreign keys (both directions), check and unique constraints of one or more tables " +
        "or views: pass several in one call. IMPORTANT: NOT included, do NOT infer: view bodies, row data or sample " +
        "values, and anything about objects in NotFound, which do not exist.")]
    public Task<TableDetailsResult> GetTableDetails(
        [Description("Tables as \"schema.name\" or \"name\" (default schema). Up to 20 per call.")]
        string[] tables,
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(DefaultSchemaHelp)]
        string? defaultSchema = null,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        [Description(ConnectionHelp)]
        string? connection = null,
        [Description(DatabaseHelp)]
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        if (tables is null || tables.Length == 0)
            throw new ArgumentException("tables must not be empty.");

        if (tables.Length > DiagnosticLimits.MaxTablesPerRequest)
            throw new ArgumentException(
                $"tables list must not exceed {DiagnosticLimits.MaxTablesPerRequest} elements (got {tables.Length}).");

        var empty = Array.FindIndex(tables, t => string.IsNullOrWhiteSpace(t));
        if (empty >= 0)
            throw new ArgumentException($"tables[{empty}] is empty or whitespace.");

        var inspector = ResolveInspector(dialect, connectionString, connection, database);

        return inspector.GetTableDetailsAsync(new TableDetailsRequest(tables, defaultSchema), cancellationToken);
    }
}
