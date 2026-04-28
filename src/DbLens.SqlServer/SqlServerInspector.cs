using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using Microsoft.Data.SqlClient;

namespace DbLens.SqlServer;

public sealed class SqlServerInspector : IDatabaseInspector
{
    private readonly string _connectionString;

    public SqlServerInspector(string connectionString)
    {
        _connectionString = connectionString;
    }

    public DatabaseDialect Dialect => DatabaseDialect.SqlServer;

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct);
        return true;
    }
}
