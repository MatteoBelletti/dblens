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
public sealed class PostgresIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public PostgresIntegrationTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    // Returns true when the test should be skipped (DB not available).
    // xUnit v2 has no dynamic skip API; tests simply return early and pass without assertions.
    private bool ShouldSkip => !_fixture.IsReachable;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_DefaultRequest_ReturnsAtLeastEightTables()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        var publicSchema = result.Schemas.Single(s => s.Name == "public");
        publicSchema.TableCount.Should().BeGreaterThanOrEqualTo(8);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_ExcludesSystemSchemas_ByDefault()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        result.Schemas.Should().NotContain(s =>
            s.Name.StartsWith("pg_") || s.Name == "information_schema");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_IncludeSystemSchemas_ContainsPgCatalog()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(
            new SchemaOverviewRequest(IncludeSystemSchemas: true));

        result.Schemas.Should().Contain(s => s.Name == "pg_catalog" || s.Name.StartsWith("pg_"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_UsersTable_HasAtLeastThreeIncomingForeignKeys()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        var users = result.Schemas
            .SelectMany(s => s.Objects)
            .Single(o => o.Name == "users" && o.Kind == DatabaseObjectKind.Table);

        // addresses (CASCADE), orders (CASCADE), audit_logs (SET NULL) all have FKs pointing to users
        users.IncomingForeignKeys.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_UsersTable_HasApproximateRowCountInExpectedRange()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        var users = result.Schemas
            .SelectMany(s => s.Objects)
            .Single(o => o.Name == "users");

        // ANALYZE was run in fixture; 20 rows seeded, reltuples is approximate
        users.ApproximateRowCount.Should().NotBeNull();
        users.ApproximateRowCount.Should().BeInRange(15, 25);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_CategoriesTable_HasOneOutgoingForeignKey()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        var categories = result.Schemas
            .SelectMany(s => s.Objects)
            .Single(o => o.Name == "categories");

        // self-referencing FK: parent_id -> id
        categories.OutgoingForeignKeys.Should().Be(1);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_AllPopulatedTables_HavePositiveTotalSizeBytes()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        var tables = result.Schemas
            .SelectMany(s => s.Objects)
            .Where(o => o.Kind == DatabaseObjectKind.Table);

        tables.Should().OnlyContain(o => o.TotalSizeBytes > 0);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_NamePatternFilter_ReturnsOnlyMatchingObjects()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(
            new SchemaOverviewRequest(NamePattern: "order%"));

        var names = result.Schemas.SelectMany(s => s.Objects).Select(o => o.Name).ToList();
        names.Should().NotBeEmpty();
        names.Should().OnlyContain(n => n.StartsWith("order"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_NamePatternOrder_ReturnsTwoResults()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(
            new SchemaOverviewRequest(NamePattern: "order%"));

        result.Schemas.SelectMany(s => s.Objects).Should().HaveCount(2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_SchemasAndObjects_AreSortedAlphabetically()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        result.Schemas.Select(s => s.Name).Should().BeInAscendingOrder();
        foreach (var schema in result.Schemas)
            schema.Objects.Select(o => o.Name).Should().BeInAscendingOrder();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_SummaryString_IsNonEmpty()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        result.Summary.Should().NotBeNullOrWhiteSpace();
        result.Dialect.Should().Be("PostgreSql");
        result.Database.Should().Be("dblens_demo");
        result.GeneratedAt.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_AllObjects_HaveTotalSizeHuman()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        result.Schemas.SelectMany(s => s.Objects)
            .Should().OnlyContain(o => !string.IsNullOrEmpty(o.TotalSizeHuman));
    }
}

/// <summary>xUnit fixture that opens a PostgreSQL connection and runs ANALYZE before tests.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Host=localhost;Port=5433;Database=dblens_demo;Username=postgres;Password=devpassword";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("DBLENS_TEST_PG_CONNECTION") ?? DefaultConnectionString;

    public bool IsReachable { get; private set; }
    public PostgresInspector? Inspector { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            Inspector = new PostgresInspector(ConnectionString, NullLogger<PostgresInspector>.Instance);
            IsReachable = await Inspector.TestConnectionAsync(
                new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token);

            if (!IsReachable) return;

            // Ensure reltuples is populated for deterministic row count assertions.
            await using var conn = new NpgsqlConnection(ConnectionString);
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
