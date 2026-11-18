using System.Text.RegularExpressions;
using DbLens.Core.Abstractions;
using DbLens.Postgres;
using DbLens.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace DbLens.Tests;

/// <summary>
/// One database per engine with a defect of each kind: the shop schema from the EF Core scripts plus a
/// column added by hand (shop.users.loyalty_code), and hygiene.subscriptions / hygiene.stock_levels.
/// </summary>
public interface IHygieneFixture
{
    bool IsReachable { get; }

    IDatabaseInspector? Inspector { get; }

    string Provider { get; }
}

[CollectionDefinition(Name)]
public sealed class HygieneDatabases : ICollectionFixture<PostgresHygieneFixture>, ICollectionFixture<SqlServerHygieneFixture>
{
    public const string Name = "Hygiene databases";
}

[Collection(HygieneDatabases.Name)]
public sealed class PostgresHygieneTests(PostgresHygieneFixture fixture) : HygieneTestsBase(fixture);

[Collection(HygieneDatabases.Name)]
public sealed class SqlServerHygieneTests(SqlServerHygieneFixture fixture) : HygieneTestsBase(fixture);

/// <summary>
/// subscriptions: 3,000 rows, 1,200 created in the last 89 days. fax_number is never filled, channel is
/// always 'web', coupon_code only on old rows, notes_old is named as left behind, updated_at equals
/// created_at although status is updated. stock_levels: 1,500 rows, no audit dates, updated.
/// </summary>
internal static class HygieneData
{
    public const string Database = "dblens_hygiene";

    /// <summary>The one value of the constant column: it must never appear in a result.</summary>
    public const string ConstantValue = "web";
}

public sealed class PostgresHygieneFixture : IAsyncLifetime, IHygieneFixture
{
    private const string SetupSql = """
        ALTER TABLE shop.users ADD COLUMN loyalty_code varchar(20);
        CREATE SCHEMA hygiene;
        CREATE TABLE hygiene.subscriptions (
            id int PRIMARY KEY, status varchar(20) NOT NULL, channel varchar(10) NOT NULL,
            fax_number varchar(20), coupon_code varchar(20), notes_old text,
            created_at timestamp NOT NULL, updated_at timestamp);
        INSERT INTO hygiene.subscriptions
        SELECT g, 'active', 'web', NULL,
               CASE WHEN g > 1200 THEN 'C' || g END, 'note ' || g, t, t
        FROM generate_series(1, 3000) g,
             LATERAL (SELECT CASE WHEN g <= 1200 THEN now()::timestamp - (g % 89) * interval '1 day'
                                  ELSE now()::timestamp - (100 + g % 600) * interval '1 day' END AS t) d;
        UPDATE hygiene.subscriptions SET status = 'paused' WHERE id % 3 = 0;
        CREATE TABLE hygiene.stock_levels (id int PRIMARY KEY, product_code varchar(20) NOT NULL, quantity int NOT NULL);
        INSERT INTO hygiene.stock_levels SELECT g, 'P' || g, g % 50 FROM generate_series(1, 1500) g;
        UPDATE hygiene.stock_levels SET quantity = quantity + 1 WHERE id % 2 = 0;
        ANALYZE;
        """;

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("DBLENS_TEST_PG_ADMIN_CONNECTION")
        ?? "Host=localhost;Port=5433;Database=dblens_demo;Username=postgres;Password=devpassword";

    private static string On =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = HygieneData.Database, Pooling = false }.ToString();

    public bool IsReachable { get; private set; }

    public IDatabaseInspector? Inspector { get; private set; }

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

        await ExecuteAsync(AdminConnectionString, $"DROP DATABASE IF EXISTS {HygieneData.Database} WITH (FORCE)");
        await ExecuteAsync(AdminConnectionString, $"CREATE DATABASE {HygieneData.Database}");
        await ExecuteAsync(On, EfScripts.Read(Provider, "Init"));
        await ExecuteAsync(On, EfScripts.Read(Provider, "AddPayments"));
        await ExecuteAsync(On, SetupSql);
        await IndexWorkload.OnPostgresAsync(On, "hygiene.stock_levels");

        Inspector = new PostgresInspector(On, NullLogger<PostgresInspector>.Instance);
        IsReachable = true;
    }

    public async Task DisposeAsync()
    {
        if (IsReachable) await ExecuteAsync(AdminConnectionString, $"DROP DATABASE IF EXISTS {HygieneData.Database} WITH (FORCE)");
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

public sealed partial class SqlServerHygieneFixture : IAsyncLifetime, IHygieneFixture
{
    private static readonly string[] SetupSql =
    [
        "ALTER TABLE shop.users ADD loyalty_code varchar(20) NULL",
        "CREATE SCHEMA hygiene",
        """
        CREATE TABLE hygiene.subscriptions (
            id int PRIMARY KEY, status varchar(20) NOT NULL, channel varchar(10) NOT NULL,
            fax_number varchar(20) NULL, coupon_code varchar(20) NULL, notes_old nvarchar(max) NULL,
            created_at datetime NOT NULL, updated_at datetime NULL)
        """,
        """
        WITH n AS (SELECT TOP (3000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS g FROM sys.all_objects a CROSS JOIN sys.all_objects b),
             d AS (SELECT g, CASE WHEN g <= 1200 THEN DATEADD(DAY, -(g % 89), GETDATE())
                                  ELSE DATEADD(DAY, -(100 + g % 600), GETDATE()) END AS t FROM n)
        INSERT INTO hygiene.subscriptions
        SELECT g, 'active', 'web', NULL, CASE WHEN g > 1200 THEN CONCAT('C', g) END, CONCAT('note ', g), t, t FROM d
        """,
        "UPDATE hygiene.subscriptions SET status = 'paused' WHERE id % 3 = 0",
        "CREATE TABLE hygiene.stock_levels (id int PRIMARY KEY, product_code varchar(20) NOT NULL, quantity int NOT NULL)",
        """
        WITH n AS (SELECT TOP (1500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS g FROM sys.all_objects a CROSS JOIN sys.all_objects b)
        INSERT INTO hygiene.stock_levels SELECT g, CONCAT('P', g), g % 50 FROM n
        """,
        "UPDATE hygiene.stock_levels SET quantity = quantity + 1 WHERE id % 2 = 0",
        // Statistics exist only on columns something filtered on: the application's queries create them.
        "SELECT COUNT(*) FROM hygiene.subscriptions WHERE fax_number IS NOT NULL OR channel = 'x' OR coupon_code = 'x' OR notes_old = 'x'",
        "UPDATE STATISTICS hygiene.subscriptions WITH FULLSCAN",
        "UPDATE STATISTICS hygiene.stock_levels WITH FULLSCAN",
    ];

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("DBLENS_TEST_SS_ADMIN_CONNECTION")
        ?? "Server=localhost,1434;Database=dblens_demo;User Id=sa;Password=DevPassword123!;TrustServerCertificate=true";

    private static string On =>
        new SqlConnectionStringBuilder(AdminConnectionString) { InitialCatalog = HygieneData.Database, Pooling = false }.ToString();

    private static string Drop =>
        $"IF DB_ID('{HygieneData.Database}') IS NOT NULL BEGIN ALTER DATABASE {HygieneData.Database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {HygieneData.Database}; END";

    public bool IsReachable { get; private set; }

    public IDatabaseInspector? Inspector { get; private set; }

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

        await ExecuteAsync(AdminConnectionString, Drop);
        await ExecuteAsync(AdminConnectionString, $"CREATE DATABASE {HygieneData.Database}");
        foreach (var script in new[] { "Init", "AddPayments" })
            foreach (var batch in GoLine().Split(EfScripts.Read(Provider, script)).Where(b => !string.IsNullOrWhiteSpace(b)))
                await ExecuteAsync(On, batch);
        foreach (var statement in SetupSql)
            await ExecuteAsync(On, statement);
        await IndexWorkload.OnSqlServerAsync(On, "hygiene.stock_levels");

        Inspector = new SqlServerInspector(On, NullLogger<SqlServerInspector>.Instance);
        IsReachable = true;
    }

    public async Task DisposeAsync()
    {
        if (IsReachable) await ExecuteAsync(AdminConnectionString, Drop);
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
