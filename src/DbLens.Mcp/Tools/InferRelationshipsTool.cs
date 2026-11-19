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
        "Maps how tables join, including references the schema does not declare (legacy, ERP). Each is Declared, " +
        "Inferred, Verified or VerifiedWithOrphans. Inferred ones come from the joins of the database's views, routines " +
        "and query history, then from names and types (x_id, id_x, cod_x, English and Italian plurals); ambiguous " +
        "columns are skipped. IMPORTANT: without verifyCandidates inferred references are SIGNALS, hypotheses. " +
        "verifyCandidates runs one read-only, rolled-back orphan count per candidate and moves the measured ones to " +
        "FINDINGS, dropping mostly-orphan paths: counts only, never row values. Matching values are evidence, not " +
        "intent: declare no constraint without the schema owner's confirmation. Do NOT infer beyond the response.")]
    public Task<InferRelationshipsResult> InferRelationships(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(SchemaHelp)]
        string? schema = null,
        [Description("Table names without schema prefix. Null = all. Both sides of a reference must be in scope.")]
        string[]? tables = null,
        [Description(
            "When true, EXECUTES read-only aggregate queries to test the candidates: counts only.")]
        bool verifyCandidates = false,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        return ResolveInspector(dialect, connectionString).InferRelationshipsAsync(
            new InferRelationshipsRequest(schema, tables, VerifyCandidates: verifyCandidates),
            cancellationToken);
    }
}
