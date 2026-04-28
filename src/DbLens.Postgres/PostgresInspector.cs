using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using Npgsql;

namespace DbLens.Postgres;

public sealed class PostgresInspector : IDatabaseInspector
{
    private readonly string _connectionString;

    public PostgresInspector(string connectionString)
    {
        _connectionString = connectionString;
    }

    public DatabaseDialect Dialect => DatabaseDialect.PostgreSql;

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct);
        return true;
    }
}
