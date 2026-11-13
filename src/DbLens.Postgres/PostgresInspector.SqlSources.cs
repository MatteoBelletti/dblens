using System.Data.Common;
using DbLens.Core.Analysis.Relationships;

namespace DbLens.Postgres;

public sealed partial class PostgresInspector
{
    private const string ViewsAndRoutinesSql = """
        SELECT 'view', schemaname || '.' || viewname, definition
        FROM pg_views
        WHERE schemaname NOT IN ('pg_catalog', 'information_schema')
        UNION ALL
        SELECT 'view', schemaname || '.' || matviewname, definition
        FROM pg_matviews
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
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sources = new List<SqlSource>();
        var caveats = new List<string>();

        await using (var cmd = Command(conn, ViewsAndRoutinesSql))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (!reader.IsDBNull(2))
                    sources.Add(new SqlSource(
                        reader.GetString(0) == "view" ? SqlSourceKind.View : SqlSourceKind.Routine,
                        reader.GetString(1), reader.GetString(2)));

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
            await using var cmd = Command(conn, WorkloadSql);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                sources.Add(new SqlSource(SqlSourceKind.Workload, "pg_stat_statements", reader.GetString(0), reader.GetInt64(1)));
        }
        catch (DbException ex)
        {
            caveats.Add($"pg_stat_statements could not be read ({ex.Message.Trim()}); query history was not used.");
        }

        return new SqlSourceSet(sources, caveats);
    }

    private static DbCommand Command(DbConnection conn, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = QueryTimeoutSeconds;
        return cmd;
    }
}
