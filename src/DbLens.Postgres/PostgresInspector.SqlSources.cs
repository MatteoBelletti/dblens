using System.Data.Common;
using DbLens.Core.Analysis.Relationships;
using DbLens.Core.Inspection;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    private const string ViewsAndRoutinesSql = """
        SELECT 'view' AS kind, schemaname || '.' || viewname AS name, definition
        FROM pg_views
        WHERE schemaname NOT IN ('pg_catalog', 'information_schema') AND definition IS NOT NULL
        UNION ALL
        SELECT 'view', schemaname || '.' || matviewname, definition
        FROM pg_matviews
        WHERE definition IS NOT NULL
        UNION ALL
        SELECT 'routine', n.nspname || '.' || p.proname, p.prosrc
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        JOIN pg_language l ON l.oid = p.prolang
        WHERE l.lanname IN ('sql', 'plpgsql')
          AND n.nspname NOT IN ('pg_catalog', 'information_schema')
          AND n.nspname NOT LIKE 'pg\_%'
        """;

    // {0}: the extension's schema.
    private const string WorkloadSql = """
        SELECT query, calls
        FROM {0}.pg_stat_statements
        WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
        ORDER BY calls DESC
        LIMIT 1000
        """;

    protected override async Task<SqlSourceSet> QuerySqlSourcesAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        var sources = await ReadAllAsync(Command(conn, ViewsAndRoutinesSql), r => new SqlSource(
            r.Text("kind") == "view" ? SqlSourceKind.View : SqlSourceKind.Routine, r.Text("name"), r.Text("definition")), ct);
        List<string> caveats = [];

        if (await StatementsSchemaAsync(conn, ct) is not { } schema)
        {
            caveats.Add(
                "pg_stat_statements is not installed, so query history was not used as join evidence — " +
                "only view and routine definitions were.");
            return new SqlSourceSet(sources, caveats);
        }

        try
        {
            sources.AddRange(await ReadAllAsync(StatementsCommand(conn, WorkloadSql, schema), r => new SqlSource(
                SqlSourceKind.Workload, "pg_stat_statements", r.Text("query"), r.Number("calls")), ct));
        }
        catch (DbException ex)
        {
            caveats.Add($"pg_stat_statements could not be read ({Brief(ex)}); query history was not used.");
        }

        return new SqlSourceSet(sources, caveats);
    }
}
