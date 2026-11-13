using System.ComponentModel;
using DbLens.Core.Analysis.Relationships;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class InferRelationshipsTool(IServiceProvider services, IDatabaseInspectorFactory factory)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "Maps how tables join, including references the schema does not declare (common in legacy and ERP " +
        "databases). Each relationship is Declared, Inferred, Verified or VerifiedWithOrphans. Inferred ones come " +
        "from joins the database's own views, routines and query history (pg_stat_statements, Query Store) perform, " +
        "then from names and types (x_id, id_x, cod_x, English and Italian plurals); ambiguous columns are skipped. " +
        "IMPORTANT: with verifyCandidates = false inferred references are SIGNALS — hypotheses, not relationships. " +
        "With verifyCandidates = true dblens runs one generated, read-only, rolled-back query per candidate that " +
        "counts orphan values and moves the measured ones into FINDINGS; a path with mostly orphans is dropped. " +
        "Only COUNTS are returned, never row values. Matching values are evidence, not intent: do not declare " +
        "a constraint without the schema owner's confirmation. " +
        "Do NOT infer relationships, columns or values beyond what this tool returns.")]
    public Task<InferRelationshipsResult> InferRelationships(
        [Description("Database dialect: PostgreSql or SqlServer.")]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("Schema to analyze. Null = all user schemas.")]
        string? schema = null,
        [Description("Table names without schema prefix. Null = all. Both sides of a reference must be in scope.")]
        string[]? tables = null,
        [Description(
            "When true, EXECUTES read-only aggregate queries to test the candidates. Counts only. Default false.")]
        bool verifyCandidates = false,
        [Description("Connection string. Omit to use the server's default for the dialect.")]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        return ResolveInspector(dialect, connectionString).InferRelationshipsAsync(
            new InferRelationshipsRequest(schema, tables, VerifyCandidates: verifyCandidates),
            cancellationToken);
    }
}
