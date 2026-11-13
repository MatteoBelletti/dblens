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
        "Looks for redundancy in table structure: repeating groups (phone1/phone2), cached derived values " +
        "(order_count, total, full_name), columns copied across FK-linked tables, list-valued text columns " +
        "(tags) and very wide tables. " +
        "IMPORTANT: the analysis reads names and types only. With verifySignals = false every result is in " +
        "SIGNALS and Findings is empty — present them as candidates to review, not problems found. " +
        "With verifySignals = true dblens runs one generated, read-only, rolled-back aggregate query per " +
        "checkable signal and moves the measured ones into FINDINGS. Those queries return COUNTS ONLY: you " +
        "cannot know which rows differ or what they contain. A divergence means the values differ, not that " +
        "the column is wrong — a deliberate snapshot looks identical to a stale cache. " +
        "SqlFix.kind 'ReadOnlyProbe' is a safe aggregate SELECT; 'Ddl' changes the database and needs review. " +
        "Do NOT infer results, columns or values beyond what this tool returns.")]
    public Task<AnalyzeDenormalizationResult> AnalyzeDenormalization(
        [Description("Database dialect: PostgreSql or SqlServer.")]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("Schema to analyze. Null = all user schemas.")]
        string? schema = null,
        [Description("Table names without schema prefix. Null = all tables in scope.")]
        string[]? tables = null,
        [Description(
            "When true, EXECUTES read-only aggregate queries to measure the signals. Counts only, never row values. " +
            "Default false: nothing is executed.")]
        bool verifySignals = false,
        [Description("Connection string. Omit to use the server's default for the dialect.")]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        return ResolveInspector(dialect, connectionString).AnalyzeDenormalizationAsync(
            new AnalyzeDenormalizationRequest(schema, tables, VerifySignals: verifySignals),
            cancellationToken);
    }
}
