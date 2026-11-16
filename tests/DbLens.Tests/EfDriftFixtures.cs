using System.Text.RegularExpressions;
using DbLens.Core.Abstractions;
using DbLens.Postgres;
using DbLens.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace DbLens.Tests;

/// <summary>One instance of each fixture for every class that uses it: they create databases by name.</summary>
[CollectionDefinition(Name)]
public sealed class EfDatabases : ICollectionFixture<PostgresEfFixture>, ICollectionFixture<SqlServerEfFixture>
{
    public const string Name = "EF databases";
}

[Collection(EfDatabases.Name)]
public sealed class PostgresEfDriftTests(PostgresEfFixture fixture) : EfDriftTestsBase(fixture);

[Collection(EfDatabases.Name)]
public sealed class SqlServerEfDriftTests(SqlServerEfFixture fixture) : EfDriftTestsBase(fixture);

/// <summary>The SQL scripts `dotnet ef` wrote for the demo model, as the fixtures apply them.</summary>
internal static class EfScripts
{
    public static string Read(string provider, string migration) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "EfSnapshots", "ef10", provider, migration + ".sql"));
}

public sealed class PostgresEfFixture : IAsyncLifetime, IEfFixture
{
    private const string DriftedDb = "dblens_ef";
    private const string CleanDb = "dblens_ef_clean";

    private const string DriftSql = $"""
        ALTER TABLE shop.users DROP COLUMN phone;
        ALTER TABLE shop.order_items DROP CONSTRAINT "FK_order_items_products_product_id";
        CREATE INDEX ix_orders_created_manual ON shop.orders (created_at);
        CREATE TABLE shop.legacy_notes (id int PRIMARY KEY);
        INSERT INTO "__EFMigrationsHistory" VALUES ('{EfDriftTestsBase.Hotfix}', '10.0.15');
        """;

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("DBLENS_TEST_PG_ADMIN_CONNECTION")
        ?? "Host=localhost;Port=5433;Database=dblens_demo;Username=postgres;Password=devpassword";

    private static string On(string database) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = database, Pooling = false }.ToString();

    public bool IsReachable { get; private set; }

    public IDatabaseInspector? Drifted { get; private set; }

    public IDatabaseInspector? Clean { get; private set; }

    public string Provider => "PostgreSql";

    public async Task InitializeAsync()
    {
        try
        {
            await ExecuteAsync(AdminConnectionString, "SELECT 1");
        }
        catch
        {
            return;
        }

        await Recreate(DriftedDb);
        await ExecuteAsync(On(DriftedDb), EfScripts.Read(Provider, "Init"));
        await ExecuteAsync(On(DriftedDb), DriftSql);

        await Recreate(CleanDb);
        await ExecuteAsync(On(CleanDb), EfScripts.Read(Provider, "Init"));
        await ExecuteAsync(On(CleanDb), EfScripts.Read(Provider, "AddPayments"));

        Drifted = new PostgresInspector(On(DriftedDb), NullLogger<PostgresInspector>.Instance);
        Clean = new PostgresInspector(On(CleanDb), NullLogger<PostgresInspector>.Instance);
        IsReachable = true;
    }

    public async Task DisposeAsync()
    {
        if (!IsReachable) return;
        await ExecuteAsync(AdminConnectionString, $"DROP DATABASE IF EXISTS {DriftedDb} WITH (FORCE)");
        await ExecuteAsync(AdminConnectionString, $"DROP DATABASE IF EXISTS {CleanDb} WITH (FORCE)");
    }

    private static async Task Recreate(string database)
    {
        await ExecuteAsync(AdminConnectionString, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        await ExecuteAsync(AdminConnectionString, $"CREATE DATABASE {database}");
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 5 }.ToString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync();
    }
}

public sealed partial class SqlServerEfFixture : IAsyncLifetime, IEfFixture
{
    private const string DriftedDb = "dblens_ef";
    private const string CleanDb = "dblens_ef_clean";

    private const string DriftSql = $"""
        ALTER TABLE shop.users DROP COLUMN phone;
        ALTER TABLE shop.order_items DROP CONSTRAINT [FK_order_items_products_product_id];
        CREATE INDEX ix_orders_created_manual ON shop.orders (created_at);
        CREATE TABLE shop.legacy_notes (id int PRIMARY KEY);
        INSERT INTO [__EFMigrationsHistory] VALUES ('{EfDriftTestsBase.Hotfix}', '10.0.15');
        """;

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("DBLENS_TEST_SS_ADMIN_CONNECTION")
        ?? "Server=localhost,1434;Database=dblens_demo;User Id=sa;Password=DevPassword123!;TrustServerCertificate=true";

    private static string On(string database) =>
        new SqlConnectionStringBuilder(AdminConnectionString) { InitialCatalog = database, Pooling = false }.ToString();

    public bool IsReachable { get; private set; }

    public IDatabaseInspector? Drifted { get; private set; }

    public IDatabaseInspector? Clean { get; private set; }

    public string Provider => "SqlServer";

    public async Task InitializeAsync()
    {
        try
        {
            await ExecuteAsync(AdminConnectionString, "SELECT 1");
        }
        catch
        {
            return;
        }

        await Recreate(DriftedDb);
        await ExecuteScriptAsync(On(DriftedDb), EfScripts.Read(Provider, "Init"));
        await ExecuteAsync(On(DriftedDb), DriftSql);

        await Recreate(CleanDb);
        await ExecuteScriptAsync(On(CleanDb), EfScripts.Read(Provider, "Init"));
        await ExecuteScriptAsync(On(CleanDb), EfScripts.Read(Provider, "AddPayments"));

        Drifted = new SqlServerInspector(On(DriftedDb), NullLogger<SqlServerInspector>.Instance);
        Clean = new SqlServerInspector(On(CleanDb), NullLogger<SqlServerInspector>.Instance);
        IsReachable = true;
    }

    public async Task DisposeAsync()
    {
        if (!IsReachable) return;
        await ExecuteAsync(AdminConnectionString, Drop(DriftedDb));
        await ExecuteAsync(AdminConnectionString, Drop(CleanDb));
    }

    private static string Drop(string database) =>
        $"IF DB_ID('{database}') IS NOT NULL BEGIN ALTER DATABASE {database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {database}; END";

    private static async Task Recreate(string database)
    {
        await ExecuteAsync(AdminConnectionString, Drop(database));
        await ExecuteAsync(AdminConnectionString, $"CREATE DATABASE {database}");
    }

    /// <summary>An EF script for SQL Server is a series of batches separated by GO lines.</summary>
    private static async Task ExecuteScriptAsync(string connectionString, string script)
    {
        foreach (var batch in GoLine().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
            await ExecuteAsync(connectionString, batch);
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync();
    }
}
