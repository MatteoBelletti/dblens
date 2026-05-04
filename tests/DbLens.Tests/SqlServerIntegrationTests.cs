using DbLens.Core.Models;
using DbLens.SqlServer;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for SqlServerInspector.GetSchemaOverviewAsync.
/// Requires the dblens-sqlserver container to be running (docker-compose up sqlserver).
/// Skipped automatically when SQL Server is not reachable.
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
public sealed class SqlServerIntegrationTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public SqlServerIntegrationTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private bool ShouldSkip => !_fixture.IsReachable;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_DefaultRequest_ReturnsAtLeastEightTables()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        var dboSchema = result.Schemas.Single(s => s.Name == "dbo");
        dboSchema.TableCount.Should().BeGreaterThanOrEqualTo(8);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_ExcludesSystemSchemas_ByDefault()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        result.Schemas.Should().NotContain(s =>
            s.Name == "sys" || s.Name == "INFORMATION_SCHEMA" || s.Name.StartsWith("db_"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_IncludeSystemSchemas_DoesNotThrowAndReturnsTables()
    {
        if (ShouldSkip) return;

        // In SQL Server user databases, sys.objects does not expose system catalog views,
        // so IncludeSystemSchemas has no practical effect — but it must not throw.
        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(
            new SchemaOverviewRequest(IncludeSystemSchemas: true));

        result.Schemas.Should().Contain(s => s.Name == "dbo");
        result.Schemas.SelectMany(s => s.Objects).Should().NotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_UsersTable_HasAtLeastTwoIncomingForeignKeys()
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

        // 20 rows seeded; sys.partitions.rows is updated by DML so should be accurate
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
    public async Task GetSchemaOverview_SummaryAndMetadata_AreCorrect()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        result.Summary.Should().NotBeNullOrWhiteSpace();
        result.Dialect.Should().Be("SqlServer");
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

    [Fact]
    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_ViewsExcluded_WhenIncludeViewsFalse()
    {
        if (ShouldSkip) return;

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(
            new SchemaOverviewRequest(IncludeViews: false));

        result.Schemas.SelectMany(s => s.Objects)
            .Should().OnlyContain(o => o.Kind == DatabaseObjectKind.Table);
    }
}

/// <summary>xUnit fixture for SQL Server integration tests.</summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string DefaultConnectionString =
        "Server=localhost,1434;Database=dblens_demo;User Id=sa;Password=DevPassword123!;TrustServerCertificate=true";

    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("DBLENS_TEST_SS_CONNECTION") ?? DefaultConnectionString;

    public bool IsReachable { get; private set; }
    public SqlServerInspector? Inspector { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            Inspector = new SqlServerInspector(ConnectionString, NullLogger<SqlServerInspector>.Instance);
            IsReachable = await Inspector.TestConnectionAsync(
                new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch
        {
            IsReachable = false;
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;
}
