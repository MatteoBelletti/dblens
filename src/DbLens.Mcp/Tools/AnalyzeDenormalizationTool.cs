using System.ComponentModel;
using DbLens.Core.Analysis.Denormalization;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class AnalyzeDenormalizationTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "Finds structural redundancy: repeating groups (phone1/phone2), cached derived values (order_count, total, " +
        "full_name), columns copied across FK-linked tables, list-valued text (tags) and very wide tables. IMPORTANT: " +
        "reads names and types only. Without verifySignals every result is a SIGNAL: a candidate to review, not a " +
        "problem found. verifySignals runs one read-only, rolled-back aggregate per checkable signal and moves the " +
        "measured ones to FINDINGS: counts only, never which rows or what they hold. A divergence means the values " +
        "differ, not that the column is wrong: a deliberate snapshot looks like a stale cache. SqlFix 'ReadOnlyProbe' " +
        "is a safe aggregate SELECT; 'Ddl' changes the database. Do NOT infer beyond the response.")]
    public Task<AnalyzeDenormalizationResult> AnalyzeDenormalization(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(SchemaHelp)]
        string? schema = null,
        [Description(TablesHelp)]
        string[]? tables = null,
        [Description("When true, EXECUTES read-only aggregate queries to measure the signals: counts only, never row values.")]
        bool verifySignals = false,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        return ResolveInspector(dialect, connectionString).AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(schema, tables, VerifySignals: verifySignals),
            cancellationToken);
    }
}
