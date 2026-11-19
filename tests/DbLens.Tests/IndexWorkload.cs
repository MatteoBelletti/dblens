using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbLens.Tests;

/// <summary>
/// 'unused-index' is skipped where no index has ever been scanned, as on a freshly started test
/// server. A primary-key lookup on another table stands in for the workload: the index under test
/// stays unscanned. On SQL Server it never touches orders (see CLAUDE.md).
/// </summary>
public static class IndexWorkload
{
    public static async Task OnPostgresAsync(string connectionString, string table)
    {
        // Closing the connection flushes its statistics; the tables are too small for a planner to pick an index.
        await PostgresAdmin.ExecuteAsync(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ToString(),
            $"SET enable_seqscan = off; SELECT count(*) FROM {table} WHERE id = 1");
    }

    public static Task OnSqlServerAsync(string connectionString, string table) =>
        SqlServerAdmin.ExecuteAsync(new SqlConnectionStringBuilder(connectionString) { Pooling = false }.ToString(),
            $"SELECT COUNT_BIG(*) FROM {table} WITH (INDEX(1)) WHERE id = 1");
}
