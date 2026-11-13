using System.Data.Common;
using DbLens.Core.Analysis.Relationships;

namespace DbLens.SqlServer;

public sealed partial class SqlServerInspector
{
    private const string ModulesSql = """
        SELECT o.type, s.name + '.' + o.name, m.definition
        FROM sys.sql_modules m
        JOIN sys.objects o ON o.object_id = m.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE o.is_ms_shipped = 0
          AND o.type IN ('V', 'P', 'FN', 'IF', 'TF', 'TR')
        """;

    // query_sql_text is nvarchar(max), which cannot be grouped on, hence the derived table.
    private const string WorkloadSql = """
        SELECT TOP (1000) qt.query_sql_text, x.executions
        FROM (
            SELECT q.query_text_id, SUM(rs.count_executions) AS executions
            FROM sys.query_store_query q
            JOIN sys.query_store_plan p ON p.query_id = q.query_id
            JOIN sys.query_store_runtime_stats rs ON rs.plan_id = p.plan_id
            GROUP BY q.query_text_id
        ) x
        JOIN sys.query_store_query_text qt ON qt.query_text_id = x.query_text_id
        ORDER BY x.executions DESC
        """;

    protected override async Task<SqlSourceSet> QuerySqlSourcesAsync(CancellationToken ct)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);

        var sources = new List<SqlSource>();
        var caveats = new List<string>();

        await using (var cmd = Command(conn, ModulesSql))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                if (!reader.IsDBNull(2))
                    sources.Add(new SqlSource(
                        reader.GetString(0).Trim() == "V" ? SqlSourceKind.View : SqlSourceKind.Routine,
                        reader.GetString(1), reader.GetString(2)));

        try
        {
            await using (var state = Command(conn, "SELECT actual_state_desc FROM sys.database_query_store_options"))
            {
                if (await state.ExecuteScalarAsync(ct) is not string s || s == "OFF")
                {
                    caveats.Add(
                        "Query Store is off, so query history was not used as join evidence — only view and " +
                        "routine definitions were.");
                    return new SqlSourceSet(sources, caveats);
                }
            }

            await using var cmd = Command(conn, WorkloadSql);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                sources.Add(new SqlSource(
                    SqlSourceKind.Workload, "Query Store", reader.GetString(0), Convert.ToInt64(reader.GetValue(1))));
        }
        catch (DbException ex)
        {
            caveats.Add($"Query Store could not be read ({ex.Message.Trim()}); query history was not used.");
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
