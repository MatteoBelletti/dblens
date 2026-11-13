using System.ComponentModel;
using DbLens.Core.Analysis.Glossary;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using ModelContextProtocol.Server;

namespace DbLens.Mcp.Tools;

[McpServerToolType]
public sealed class BuildGlossaryTool(
    IServiceProvider services, IDatabaseInspectorFactory factory, CertifiedQueryStore store)
    : InspectorTool(services, factory)
{
    [McpServerTool, Description(
        "Generates a data map of the database as a ready SKILL.md: every table with its documented meaning, " +
        "columns, join paths labelled declared / inferred / verified, candidate measures, how often the database's " +
        "own SQL uses each table, and the user's certified queries. Pass writeSkillTo to save it as a skill a client " +
        "loads before writing SQL. " +
        "IMPORTANT: meanings come only from schema comments. An undocumented column is shown as '—' — do NOT " +
        "invent a meaning from its name; ask the user. verifyJoins runs counts-only, rolled-back probes on inferred " +
        "joins. No row values are included.")]
    public async Task<GlossaryResult> BuildGlossary(
        [Description("Database dialect: PostgreSql or SqlServer.")]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description("Schema to map. Null = all user schemas.")]
        string? schema = null,
        [Description("Table names without schema prefix. Null = all tables in scope.")]
        string[]? tables = null,
        [Description("Test inferred joins against the data (counts only). Default false.")]
        bool verifyJoins = false,
        [Description("Absolute directory to write SKILL.md into, e.g. the project's .claude/skills/erp-data-map. Omit to only return it.")]
        string? writeSkillTo = null,
        [Description("Replace an existing SKILL.md in writeSkillTo. Default false.")]
        bool overwrite = false,
        [Description("Connection string. Omit to use the server's default for the dialect.")]
        string? connectionString = null,
        CancellationToken cancellationToken = default)
    {
        var inspector = ResolveInspector(dialect, connectionString);
        var certified = CertifiedQueryStore.ForGlossary(store.List(dialect, inspector.DatabaseName));

        var result = await inspector.BuildGlossaryAsync(
            new GlossaryRequest(schema, tables, verifyJoins, certified), cancellationToken);

        if (writeSkillTo is null) return result;

        // The server's working directory is wherever the client started it, so a relative path
        // would land somewhere nobody expects.
        if (!Path.IsPathFullyQualified(writeSkillTo))
            throw new ArgumentException("writeSkillTo must be an absolute directory.");

        var path = Path.Combine(writeSkillTo, "SKILL.md");
        if (File.Exists(path) && !overwrite)
            throw new ArgumentException($"{path} already exists. Pass overwrite = true to replace it.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, result.Markdown, cancellationToken);
        return result with { WrittenTo = path };
    }
}
