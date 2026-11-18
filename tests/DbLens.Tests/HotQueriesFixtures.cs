using DbLens.Core.Abstractions;
using DbLens.Core.Analysis.HotQueries;
using DbLens.Postgres;
using DbLens.SqlServer;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace DbLens.Tests;

[CollectionDefinition(Name)]
public sealed class HotDatabases : ICollectionFixture<PostgresHotFixture>, ICollectionFixture<SqlServerHotFixture>
{
    public const string Name = "Hot-query databases";
}

[Collection(HotDatabases.Name)]
public sealed class PostgresHotQueriesTests(PostgresHotFixture fixture) : HotQueriesTestsBase(fixture);

[Collection(HotDatabases.Name)]
public sealed class SqlServerHotQueriesTests(SqlServerHotFixture fixture) : HotQueriesTestsBase(fixture)
{
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task WithoutQueryStore_ThePlanCacheIsRead_AndTheGapSaid()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // Changing a database option empties its plan cache, so the workload runs again first.
        await fixture.SetQueryStoreAsync(on: false);
        try
        {
            await fixture.RunAsync(HotWorkload.Selective);
            var result = await Find();

            result.Availability.Source.Should().Be("plan cache");
            result.Availability.Missing.Should().ContainSingle(m => m.Contains("Query Store is OFF"));
            result.Queries.Should().Contain(q => q.Tables.Contains("hot.events"));
        }
        finally
        {
            await fixture.SetQueryStoreAsync(on: true);
        }
    }
}

/// <summary>The workload both fixtures run, in each engine's spelling.</summary>
internal static class HotWorkload
{
    public const int Repeats = 3;

    public static string FanOut(string count) => $"""
        SELECT o.id, i.qty, p.amount FROM hot.orders o
        JOIN hot.order_items i ON i.order_id = o.id
        JOIN hot.payments p ON p.order_id = o.id
        WHERE o.id <= 10
        """;

    public const string Selective = "SELECT e.id, e.kind FROM hot.events e WHERE LOWER(e.kind) = 'rare'";

    public const string WithSecrets = """
        /* customer mario.rossi@example.com */
        SELECT o.id FROM hot.orders o JOIN hot.payments p ON p.order_id = o.id
        WHERE o.note = 'Mario Rossi' AND p.amount > 4242.42
        """;
}

public sealed class PostgresHotFixture : IAsyncLifetime, IHotFixture
{
    private const string Database = "dblens_hot";

    private const string SetupSql = """
        CREATE EXTENSION pg_stat_statements;
        CREATE SCHEMA hot;
        CREATE TABLE hot.orders (id int PRIMARY KEY, note text);
        CREATE TABLE hot.order_items (id int PRIMARY KEY, order_id int NOT NULL REFERENCES hot.orders (id), qty int);
        CREATE TABLE hot.payments (id int PRIMARY KEY, order_id int NOT NULL REFERENCES hot.orders (id), amount numeric);
        CREATE TABLE hot.events (id int PRIMARY KEY, kind text NOT NULL);
        INSERT INTO hot.orders SELECT g, 'order ' || g FROM generate_series(1, 120) g;
        INSERT INTO hot.order_items SELECT g, (g - 1) / 20 + 1, 1 FROM generate_series(1, 2400) g;
        INSERT INTO hot.payments SELECT g, (g - 1) / 20 + 1, 10 FROM generate_series(1, 2400) g;
        INSERT INTO hot.events SELECT g, CASE WHEN g % 40000 = 0 THEN 'rare' ELSE 'common' END FROM generate_series(1, 200000) g;
        ANALYZE;
        """;

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("DBLENS_TEST_PG_ADMIN_CONNECTION")
        ?? "Host=localhost;Port=5433;Database=dblens_demo;Username=postgres;Password=devpassword";

    private static string HotConnectionString =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = Database, Pooling = false }.ToString();

    public bool IsReachable { get; private set; }

    public IDatabaseInspector? Inspector { get; private set; }

    public string ExpectedSource => "pg_stat_statements";

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

        await ExecuteAsync(AdminConnectionString, $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)");
        await ExecuteAsync(AdminConnectionString, $"CREATE DATABASE {Database}");
        await ExecuteAsync(HotConnectionString, SetupSql);

        for (var i = 0; i < HotWorkload.Repeats; i++)
        {
            await ExecuteAsync(HotConnectionString, HotWorkload.FanOut("count(*)"));
            await ExecuteAsync(HotConnectionString, HotWorkload.Selective);
            await ExecuteAsync(HotConnectionString, HotWorkload.WithSecrets);
        }

        Inspector = new PostgresInspector(HotConnectionString, NullLogger<PostgresInspector>.Instance);
        IsReachable = true;
    }

    public async Task DisposeAsync()
    {
        if (IsReachable) await ExecuteAsync(AdminConnectionString, $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)");
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

public sealed class SqlServerHotFixture : IAsyncLifetime, IHotFixture
{
    private const string Database = "dblens_hot";

    private static readonly string[] SetupSql =
    [
        "CREATE SCHEMA hot",
        "CREATE TABLE hot.orders (id int PRIMARY KEY, note nvarchar(100))",
        "CREATE TABLE hot.order_items (id int PRIMARY KEY, order_id int NOT NULL REFERENCES hot.orders (id), qty int)",
        "CREATE TABLE hot.payments (id int PRIMARY KEY, order_id int NOT NULL REFERENCES hot.orders (id), amount decimal(10, 2))",
        "CREATE TABLE hot.events (id int PRIMARY KEY, kind varchar(20) NOT NULL)",
        """
        WITH n AS (SELECT TOP (200000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS g
                   FROM sys.all_objects a CROSS JOIN sys.all_objects b)
        INSERT INTO hot.events SELECT g, CASE WHEN g % 40000 = 0 THEN 'rare' ELSE 'common' END FROM n
        """,
        """
        WITH n AS (SELECT TOP (2400) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS g FROM sys.all_objects a CROSS JOIN sys.all_objects b)
        INSERT INTO hot.orders SELECT g, CONCAT('order ', g) FROM n WHERE g <= 120
        """,
        """
        WITH n AS (SELECT TOP (2400) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS g FROM sys.all_objects a CROSS JOIN sys.all_objects b)
        INSERT INTO hot.order_items SELECT g, (g - 1) / 20 + 1, 1 FROM n
        """,
        """
        WITH n AS (SELECT TOP (2400) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS g FROM sys.all_objects a CROSS JOIN sys.all_objects b)
        INSERT INTO hot.payments SELECT g, (g - 1) / 20 + 1, 10 FROM n
        """,
        "UPDATE STATISTICS hot.events",
    ];

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable("DBLENS_TEST_SS_ADMIN_CONNECTION")
        ?? "Server=localhost,1434;Database=dblens_demo;User Id=sa;Password=DevPassword123!;TrustServerCertificate=true";

    private static string HotConnectionString =>
        new SqlConnectionStringBuilder(AdminConnectionString) { InitialCatalog = Database, Pooling = false }.ToString();

    private static string Drop =>
        $"IF DB_ID('{Database}') IS NOT NULL BEGIN ALTER DATABASE {Database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Database}; END";

    public bool IsReachable { get; private set; }

    public IDatabaseInspector? Inspector { get; private set; }

    public string ExpectedSource => "Query Store";

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
        await ExecuteAsync(AdminConnectionString, $"CREATE DATABASE {Database}");
        // AUTO, the default, records only queries that run often or cost much: a test workload is neither.
        await SetQueryStoreAsync(on: true);
        foreach (var statement in SetupSql)
            await ExecuteAsync(HotConnectionString, statement);

        for (var i = 0; i < HotWorkload.Repeats; i++)
        {
            await ExecuteAsync(HotConnectionString, HotWorkload.FanOut("COUNT_BIG(*)"));
            await ExecuteAsync(HotConnectionString, HotWorkload.Selective);
            await ExecuteAsync(HotConnectionString, HotWorkload.WithSecrets);
        }

        Inspector = new SqlServerInspector(HotConnectionString, NullLogger<SqlServerInspector>.Instance);
        IsReachable = true;
    }

    public Task RunAsync(string sql) => ExecuteAsync(HotConnectionString, sql);

    public Task SetQueryStoreAsync(bool on) => ExecuteAsync(AdminConnectionString, on
        ? $"ALTER DATABASE {Database} SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE, QUERY_CAPTURE_MODE = ALL)"
        : $"ALTER DATABASE {Database} SET QUERY_STORE = OFF");

    public async Task DisposeAsync()
    {
        if (IsReachable) await ExecuteAsync(AdminConnectionString, Drop);
    }

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
