using System.ComponentModel;
using DbLens.Core.Abstractions;
using DbLens.Core.Analysis.Indexes;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class AnalyzeIndexesTool(IServiceProvider services, IDatabaseInspectorFactory factory)
{
    private IKeyedServiceProvider KeyedServices => (IKeyedServiceProvider)services;

    [McpServerTool, Description(
        "Analyzes indexes in a PostgreSQL or SQL Server database and returns rule-based diagnostic findings. " +
        "Detects four types of problems: " +
        "(1) fk-without-index — foreign key column with no covering index (severity: high); " +
        "(2) duplicate-index — two indexes on the same columns with the same uniqueness (severity: medium); " +
        "(3) redundant-index — non-unique index whose columns are an exact prefix of a longer index (severity: low); " +
        "(4) unused-index — non-unique index with zero scans since last DB restart (severity: low). " +
        "Each finding includes a severity, confidence score, affected columns, description, and a suggested SQL fix. " +
        "The response includes a Summary (counts by rule and severity) and Caveats (statistical limitations). " +
        "IMPORTANT: This tool performs rule-based analysis on schema metadata and DB statistics only. " +
        "It does NOT analyze query workloads or execution plans — do not infer that unlisted indexes cause problems. " +
        "The 'unused-index' rule relies on cumulative scan counts since the last DB restart — " +
        "always read the Caveats field before recommending index removal. " +
        "Do NOT infer, guess, or generate findings beyond what this tool returns.")]
    public Task<AnalyzeIndexesResult> AnalyzeIndexes(
        [Description("Database dialect: PostgreSql or SqlServer.")]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(
            "Schema to analyze. Null = all user schemas. " +
            "Use 'public' for PostgreSQL default schema, 'dbo' for SQL Server.")]
        string? schema = null,
        [Description(
            "Table names to include (without schema prefix). Null = all tables in the specified schema(s).")]
        string[]? tables = null,
        [Description(
            "Whether to run the unused-index rule, which queries DB scan statistics. " +
            "Set to false to perform structural analysis only (fk-without-index, duplicate, redundant).")]
        bool includeUnusedIndexes = true,
        [Description(
            "Minimum DB uptime in days required to emit unused-index findings. " +
            "Prevents false positives on recently restarted databases. Default: 7 days.")]
        int unusedIndexMinDays = 7,
        [Description(
            "Connection string to the target database. " +
            "If omitted, uses the server's configured default for the chosen dialect.")]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        var inspector = connectionString is not null
            ? factory.Create(dialect, connectionString)
            : KeyedServices.GetRequiredKeyedService<IDatabaseInspector>(dialect);

        return inspector.AnalyzeIndexesAsync(
            new AnalyzeIndexesRequest(schema, tables, includeUnusedIndexes, unusedIndexMinDays),
            cancellationToken);
    }
}
