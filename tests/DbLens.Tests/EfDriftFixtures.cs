using DbLens.Core.Abstractions;
using DbLens.Postgres;
using DbLens.SqlServer;
using Microsoft.Extensions.Logging.Abstractions;

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
    /// <summary>The folder of one EF Core major's fixtures for a provider, as scripts/generate-ef-fixtures.sh writes it.</summary>
    public static string Folder(string provider, int major = 10) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "EfSnapshots", $"ef{major}", provider);

    public static string Read(string provider, string migration) => File.ReadAllText(Path.Combine(Folder(provider), migration + ".sql"));

    /// <summary>The demo database's shop schema (db/*/04-shop.sql): Init, then the drift the tests expect.</summary>
    public static string Drifted(string provider) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "Demo", provider == "PostgreSql" ? "postgres" : "sqlserver", "04-shop.sql"));
}

public sealed class PostgresEfFixture : IAsyncLifetime, IEfFixture
{
    private const string DriftedDb = "dblens_ef";
    private const string CleanDb = "dblens_ef_clean";


    private static string On(string database) => PostgresAdmin.On(database);

    public bool IsReachable { get; private set; }

    public IDatabaseInspector? Drifted { get; private set; }

    public IDatabaseInspector? Clean { get; private set; }

    public string Provider => "PostgreSql";

    public Task ExecuteOnCleanAsync(string sql) => PostgresAdmin.ExecuteAsync(On(CleanDb), sql);

    public async Task InitializeAsync()
    {
        if (!await PostgresAdmin.IsReachableAsync()) return;

        await PostgresAdmin.RecreateAsync(DriftedDb);
        await PostgresAdmin.ExecuteAsync(On(DriftedDb), EfScripts.Drifted(Provider));

        await PostgresAdmin.RecreateAsync(CleanDb);
        await PostgresAdmin.ExecuteAsync(On(CleanDb), EfScripts.Read(Provider, "Init"));
        await PostgresAdmin.ExecuteAsync(On(CleanDb), EfScripts.Read(Provider, "AddPayments"));
        await PostgresAdmin.ExecuteAsync(On(CleanDb), EfScripts.Read(Provider, "DropUserDateOfBirth"));

        Drifted = new PostgresInspector(On(DriftedDb), NullLogger<PostgresInspector>.Instance);
        Clean = new PostgresInspector(On(CleanDb), NullLogger<PostgresInspector>.Instance);
        IsReachable = true;
    }

    public async Task DisposeAsync()
    {
        if (!IsReachable) return;
        await PostgresAdmin.DropAsync(DriftedDb);
        await PostgresAdmin.DropAsync(CleanDb);
    }

}

public sealed class SqlServerEfFixture : IAsyncLifetime, IEfFixture
{
    private const string DriftedDb = "dblens_ef";
    private const string CleanDb = "dblens_ef_clean";


    private static string On(string database) => SqlServerAdmin.On(database);

    public bool IsReachable { get; private set; }

    public IDatabaseInspector? Drifted { get; private set; }

    public IDatabaseInspector? Clean { get; private set; }

    public string Provider => "SqlServer";

    public Task ExecuteOnCleanAsync(string sql) => SqlServerAdmin.ExecuteAsync(On(CleanDb), sql);

    public async Task InitializeAsync()
    {
        if (!await SqlServerAdmin.IsReachableAsync()) return;

        await SqlServerAdmin.RecreateAsync(DriftedDb);
        await SqlServerAdmin.ExecuteScriptAsync(On(DriftedDb), EfScripts.Drifted(Provider));

        await SqlServerAdmin.RecreateAsync(CleanDb);
        await SqlServerAdmin.ExecuteScriptAsync(On(CleanDb), EfScripts.Read(Provider, "Init"));
        await SqlServerAdmin.ExecuteScriptAsync(On(CleanDb), EfScripts.Read(Provider, "AddPayments"));
        await SqlServerAdmin.ExecuteScriptAsync(On(CleanDb), EfScripts.Read(Provider, "DropUserDateOfBirth"));

        Drifted = new SqlServerInspector(On(DriftedDb), NullLogger<SqlServerInspector>.Instance);
        Clean = new SqlServerInspector(On(CleanDb), NullLogger<SqlServerInspector>.Instance);
        IsReachable = true;
    }

    public async Task DisposeAsync()
    {
        if (!IsReachable) return;
        await SqlServerAdmin.DropAsync(DriftedDb);
        await SqlServerAdmin.DropAsync(CleanDb);
    }
}
