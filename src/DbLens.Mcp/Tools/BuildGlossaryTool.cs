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
        "Generates a data map as a ready SKILL.md: each table's documented meaning, columns, join paths (declared / " +
        "inferred / verified), candidate measures, how often the database's own SQL uses it, and the certified queries. " +
        "writeSkillTo saves it as a skill. IMPORTANT: meanings come only from schema comments; '—' means undocumented: " +
        "do NOT invent a meaning from the name, ask the user. verifyJoins runs counts-only, rolled-back probes. No row " +
        "values are included.")]
    public async Task<GlossaryResult> BuildGlossary(
        [Description(DialectHelp)]
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        [Description(SchemaHelp)]
        string? schema = null,
        [Description(TablesHelp)]
        string[]? tables = null,
        [Description("Test inferred joins against the data: counts only.")]
        bool verifyJoins = false,
        [Description("Absolute directory for SKILL.md, e.g. the project's .claude/skills/erp-data-map. Omit to only return it.")]
        string? writeSkillTo = null,
        [Description("Replace an existing SKILL.md in writeSkillTo.")]
        bool overwrite = false,
        [Description(ConnectionStringHelp)]
        string? connectionString = null,
        [Description(ConnectionHelp)]
        string? connection = null,
        [Description(DatabaseHelp)]
        string? database = null,
        CancellationToken cancellationToken = default)
    {
        var inspector = ResolveInspector(dialect, connectionString, connection, database);
        var server = CertifiedQueryStore.ServerOf(inspector.Describe(TargetSource.Configured));
        var entries = store.List(inspector.Dialect, inspector.DatabaseName, server);

        var result = await inspector.BuildGlossaryAsync(
            new GlossaryRequest(schema, tables, verifyJoins, CertifiedQueryStore.ForGlossary(entries)), cancellationToken);
        var legacy = entries.Where(e => e.Server is null).Select(e => CertifiedQueryStore.LegacyCaveat(e, server)).ToList();
        if (legacy.Count > 0) result = result with { Caveats = [.. result.Caveats, .. legacy] };

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
