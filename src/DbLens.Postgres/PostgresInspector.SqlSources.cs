using System.Data.Common;
using DbLens.Core.Analysis.Relationships;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    private const string ViewsAndRoutinesSql = """
        SELECT 'view', schemaname || '.' || viewname, definition
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

    private const string WorkloadSql = """
        SELECT query, calls
        FROM pg_stat_statements
        WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
        ORDER BY calls DESC
        LIMIT 1000
        """;

    protected override async Task<SqlSourceSet> QuerySqlSourcesAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);

        var sources = await ReadAllAsync(Command(conn, ViewsAndRoutinesSql), r => new SqlSource(
            r.GetString(0) == "view" ? SqlSourceKind.View : SqlSourceKind.Routine, r.GetString(1), r.GetString(2)), ct);
        var caveats = new List<string>();

        await using (var check = Command(conn, "SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_stat_statements')"))
        {
            if (await check.ExecuteScalarAsync(ct) is not true)
            {
                caveats.Add(
                    "pg_stat_statements is not installed, so query history was not used as join evidence — " +
                    "only view and routine definitions were.");
                return new SqlSourceSet(sources, caveats);
            }
        }

        try
        {
            sources.AddRange(await ReadAllAsync(Command(conn, WorkloadSql), r => new SqlSource(
                SqlSourceKind.Workload, "pg_stat_statements", r.GetString(0), r.GetInt64(1)), ct));
        }
        catch (DbException ex)
        {
            caveats.Add($"pg_stat_statements could not be read ({ex.Message.Trim()}); query history was not used.");
        }

        return new SqlSourceSet(sources, caveats);
    }
}
