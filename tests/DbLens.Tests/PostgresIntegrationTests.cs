using DbLens.Core.Models;
using DbLens.Postgres;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for PostgresInspector.GetSchemaOverviewAsync.
/// Requires the dblens-postgres container to be running (docker-compose up postgres).
/// Skipped automatically when PostgreSQL is not reachable.
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
public sealed class PostgresIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture = fixture;

    // Returns true when the test should be skipped (DB not available).
    // xUnit v2 has no dynamic skip API; tests simply return early and pass without assertions.
    private bool ShouldSkip => !_fixture.IsReachable;


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_DefaultRequest_ReturnsAtLeastEightTables()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        var publicSchema = result.Schemas.Single(s => s.Name == "public");
        publicSchema.TableCount.Should().BeGreaterThanOrEqualTo(8);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_ExcludesSystemSchemas_ByDefault()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        result.Schemas.Should().NotContain(s =>
            s.Name.StartsWith("pg_") || s.Name == "information_schema");
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_IncludeSystemSchemas_ContainsPgCatalog()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(
            new SchemaOverviewRequest(IncludeSystemSchemas: true));

        result.Schemas.Should().Contain(s => s.Name == "pg_catalog" || s.Name.StartsWith("pg_"));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_UsersTable_HasAtLeastThreeIncomingForeignKeys()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest(SchemaFilter: "public"));

        var users = result.Schemas
            .SelectMany(s => s.Objects)
            .Single(o => o.Name == "users" && o.Kind == DatabaseObjectKind.Table);

        // addresses (CASCADE), orders (CASCADE), audit_logs (SET NULL) all have FKs pointing to users
        users.IncomingForeignKeys.Should().BeGreaterThanOrEqualTo(3);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_UsersTable_HasApproximateRowCountInExpectedRange()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest(SchemaFilter: "public"));

        var users = result.Schemas
            .SelectMany(s => s.Objects)
            .Single(o => o.Name == "users");

        // ANALYZE was run in fixture; 20 rows seeded, reltuples is approximate
        users.ApproximateRowCount.Should().NotBeNull();
        users.ApproximateRowCount.Should().BeInRange(15, 25);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_CategoriesTable_HasOneOutgoingForeignKey()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest(SchemaFilter: "public"));

        var categories = result.Schemas
            .SelectMany(s => s.Objects)
            .Single(o => o.Name == "categories");

        // self-referencing FK: parent_id -> id
        categories.OutgoingForeignKeys.Should().Be(1);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_AllPopulatedTables_HavePositiveTotalSizeBytes()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest(SchemaFilter: "public"));

        var tables = result.Schemas
            .SelectMany(s => s.Objects)
            .Where(o => o.Kind == DatabaseObjectKind.Table);

        tables.Should().OnlyContain(o => o.TotalSizeBytes > 0);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_NamePatternFilter_ReturnsOnlyMatchingObjects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(
            new SchemaOverviewRequest(NamePattern: "order%"));

        var names = result.Schemas.SelectMany(s => s.Objects).Select(o => o.Name).ToList();
        names.Should().NotBeEmpty();
        names.Should().OnlyContain(n => n.StartsWith("order"));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_NamePatternOrder_ReturnsTwoResults()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(
            new SchemaOverviewRequest(SchemaFilter: "public", NamePattern: "order%"));

        result.Schemas.SelectMany(s => s.Objects).Should().HaveCount(2);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_SchemasAndObjects_AreSortedAlphabetically()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        result.Schemas.Select(s => s.Name).Should().BeInAscendingOrder();
        foreach (var schema in result.Schemas)
            schema.Objects.Select(o => o.Name).Should().BeInAscendingOrder();
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_SummaryString_IsNonEmpty()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        result.Summary.Should().NotBeNullOrWhiteSpace();
        result.Dialect.Should().Be("PostgreSql");
        result.Database.Should().Be("dblens_demo");
        result.GeneratedAt.Should().NotBeNullOrWhiteSpace();
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_AllObjects_HaveTotalSizeHuman()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        result.Schemas.SelectMany(s => s.Objects)
            .Should().OnlyContain(o => !string.IsNullOrEmpty(o.TotalSizeHuman));
    }
}

/// <summary>xUnit fixture that opens a PostgreSQL connection and runs ANALYZE before tests.</summary>
public sealed class PostgresFixture : IAsyncLifetime, IDatabaseFixture
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5433;Database=dblens_demo;Username=dblens_reader;Password=readerpassword";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("DBLENS_TEST_PG_CONNECTION") ?? DefaultConnectionString;

    // The superuser, for setup and for asserting that a write-capable login is refused.
    private static string AdminConnectionString => PostgresAdmin.ConnectionString;

    public Task RunWorkloadAsync() => IndexWorkload.OnPostgresAsync(ConnectionString, "users");

    public DbLens.Core.Abstractions.IDatabaseInspector CreateAdminInspector(bool readOnlyLoginOnLocalhost = false) =>
        new PostgresInspector(AdminConnectionString, NullLogger<PostgresInspector>.Instance)
        {
            ReadOnlyLoginOnLocalhost = readOnlyLoginOnLocalhost,
        };

    public bool IsReachable { get; private set; }
    public PostgresInspector? Inspector { get; private set; }

    DbLens.Core.Abstractions.IDatabaseInspector? IDatabaseFixture.Inspector => Inspector;

    public string UserSchema => "public";

    public async Task InitializeAsync()
    {
        try
        {
            Inspector = new PostgresInspector(ConnectionString, NullLogger<PostgresInspector>.Instance);
            IsReachable = await Inspector.TestConnectionAsync(
                new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token);

            if (!IsReachable) return;

            // Ensure reltuples is populated for deterministic row count assertions.
            await using var conn = new NpgsqlConnection(AdminConnectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "ANALYZE";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            IsReachable = false;
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
