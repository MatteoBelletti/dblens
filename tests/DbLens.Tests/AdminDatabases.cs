using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbLens.Tests;

/// <summary>
/// The admin login, for the fixtures that create a database of their own: setup and teardown only,
/// never what a test inspects with.
/// </summary>
internal static class PostgresAdmin
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("DBLENS_TEST_PG_ADMIN_CONNECTION")
        ?? "Host=localhost;Port=5433;Database=dblens_demo;Username=postgres;Password=devpassword";

    public static string On(string database) =>
        new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database, Pooling = false }.ToString();

    /// <summary>False when the server cannot be reached: the fixture's tests then skip.</summary>
    public static async Task<bool> IsReachableAsync()
    {
        try
        {
            await ExecuteAsync(ConnectionString, "SELECT 1");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task RecreateAsync(string database)
    {
        await DropAsync(database);
        await ExecuteAsync(ConnectionString, $"CREATE DATABASE {database}");
    }

    public static Task DropAsync(string database) =>
        ExecuteAsync(ConnectionString, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 5 }.ToString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <inheritdoc cref="PostgresAdmin"/>
internal static partial class SqlServerAdmin
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("DBLENS_TEST_SS_ADMIN_CONNECTION")
        ?? "Server=localhost,1434;Database=dblens_demo;User Id=sa;Password=DevPassword123!;TrustServerCertificate=true";

    public static string On(string database) =>
        new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = database, Pooling = false }.ToString();

    /// <inheritdoc cref="PostgresAdmin.IsReachableAsync"/>
    public static async Task<bool> IsReachableAsync()
    {
        try
        {
            await ExecuteAsync(ConnectionString, "SELECT 1");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task RecreateAsync(string database, string? collation = null)
    {
        await DropAsync(database);
        await ExecuteAsync(ConnectionString, $"CREATE DATABASE {database}" + (collation is null ? "" : $" COLLATE {collation}"));
    }

    public static Task DropAsync(string database) => ExecuteAsync(ConnectionString,
        $"IF DB_ID('{database}') IS NOT NULL BEGIN ALTER DATABASE {database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {database}; END");

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 5 }.ToString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A script `dotnet ef` writes for SQL Server: batches separated by GO lines.</summary>
    public static async Task ExecuteScriptAsync(string connectionString, string script)
    {
        foreach (var batch in GoLine().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
            await ExecuteAsync(connectionString, batch);
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();
}
