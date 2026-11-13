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
        "Returns columns, indexes, foreign keys (both directions), check and unique constraints for one or more " +
        "tables or views. Pass several tables in one call. " +
        "IMPORTANT: NOT included — do NOT infer them: view SQL bodies, row data or sample values, and anything " +
        "about objects listed in NotFound, which do not exist.")]
    public Task<TableDetailsResult> GetTableDetails(
        [Description("Tables as \"schema.name\" or \"name\" (default schema). Up to 20 per call.")]
        string[] tables,
        [Description("Database dialect: PostgreSql or SqlServer.")]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("Schema for unqualified names. Defaults to public / dbo.")]
        string? defaultSchema = null,
        [Description("Connection string. Omit to use the server's default for the dialect.")]
        string? connectionString = null,
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

        var inspector = ResolveInspector(dialect, connectionString);

        return inspector.GetTableDetailsAsync(new TableDetailsRequest(tables, defaultSchema), cancellationToken);
    }
}
