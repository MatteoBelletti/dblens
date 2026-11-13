using System.ComponentModel;
using DbLens.Core.Analysis.Gdpr;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class ScanGdprTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "Flags columns whose NAMES look like personal data (identifiers, contact, financial, GDPR Art. 9 special " +
        "categories, location) in English and Italian, plus PII tables without soft-delete or audit columns. " +
        "IMPORTANT: every result is a SIGNAL and Findings is always empty — present them as columns worth " +
        "checking, never as PII found. No row data is read, so an empty result does NOT mean the database holds " +
        "no personal data: always report the Coverage block (columns the scan could not interpret) and the " +
        "Disclaimer, and never present a clean scan as evidence of compliance. " +
        "Do NOT infer results beyond what this tool returns.")]
    public Task<ScanGdprResult> ScanGdpr(
        [Description("Database dialect: PostgreSql or SqlServer.")]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("Schema to scan. Null = all user schemas.")]
        string? schema = null,
        [Description("Table names without schema prefix. Null = all tables in scope.")]
        string[]? tables = null,
        [Description("Table names to exclude, without schema prefix.")]
        string[]? excludeTables = null,
        [Description("Minimum match confidence, 0.0-1.0. Default 0.5; lower it for recall, raise it for precision.")]
        double minConfidence = 0.5,
        [Description("Connection string. Omit to use the server's default for the dialect.")]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        var inspector = ResolveInspector(dialect, connectionString);

        return inspector.ScanGdprAsync(
            new ScanGdprRequest(schema, tables, excludeTables, minConfidence),
            cancellationToken);
    }
}
