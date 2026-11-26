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
        "Flags columns whose NAMES look like personal data (identifiers, contact, financial, GDPR Art. 9 categories, " +
        "location; English and Italian) and PII tables without soft-delete or audit columns. IMPORTANT: every result is " +
        "a SIGNAL and Findings is always empty: columns worth checking, never PII found. No row data is read, so an " +
        "empty result does NOT mean no personal data: always report Coverage (columns it could not interpret) and the " +
        "Disclaimer, and never present a clean scan as evidence of compliance. Do NOT infer beyond the response.")]
    public Task<ScanGdprResult> ScanGdpr(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(SchemaHelp)]
        string? schema = null,
        [Description(TablesHelp)]
        string[]? tables = null,
        [Description("Table names to exclude, without schema prefix.")]
        string[]? excludeTables = null,
        [Description("Minimum match confidence, 0.0-1.0: lower for recall, higher for precision.")]
        double minConfidence = 0.5,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        [Description(ConnectionHelp)]
        string? connection = null,
        [Description(DatabaseHelp)]
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        var inspector = ResolveInspector(dialect, connectionString, connection, database);

        return inspector.ScanGdprAsync(
            new ScanGdprRequest(schema, tables, excludeTables, minConfidence),
            cancellationToken);
    }
}
