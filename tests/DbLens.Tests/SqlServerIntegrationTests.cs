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
public sealed class SqlServerIntegrationTests(SqlServerFixture fixture) : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture = fixture;

    private bool ShouldSkip => !_fixture.IsReachable;


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_DefaultRequest_ReturnsAtLeastEightTables()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        var dboSchema = result.Schemas.Single(s => s.Name == "dbo");
        dboSchema.TableCount.Should().BeGreaterThanOrEqualTo(8);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_ExcludesSystemSchemas_ByDefault()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        result.Schemas.Should().NotContain(s =>
            s.Name == "sys" || s.Name == "INFORMATION_SCHEMA" || s.Name.StartsWith("db_"));
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_IncludeSystemSchemas_DoesNotThrowAndReturnsTables()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // In SQL Server user databases, sys.objects does not expose system catalog views,
        // so IncludeSystemSchemas has no practical effect — but it must not throw.
        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(
            new SchemaOverviewRequest(IncludeSystemSchemas: true));

        result.Schemas.Should().Contain(s => s.Name == "dbo");
        result.Schemas.SelectMany(s => s.Objects).Should().NotBeEmpty();
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_UsersTable_HasAtLeastTwoIncomingForeignKeys()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest(SchemaFilter: "dbo"));

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

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest(SchemaFilter: "dbo"));

        var users = result.Schemas
            .SelectMany(s => s.Objects)
            .Single(o => o.Name == "users");

        // 20 rows seeded; sys.partitions.rows is updated by DML so should be accurate
        users.ApproximateRowCount.Should().NotBeNull();
        users.ApproximateRowCount.Should().BeInRange(15, 25);
    }


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_CategoriesTable_HasOneOutgoingForeignKey()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest(SchemaFilter: "dbo"));

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

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest(SchemaFilter: "dbo"));

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
            new SchemaOverviewRequest(SchemaFilter: "dbo", NamePattern: "order%"));

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
    public async Task GetSchemaOverview_SummaryAndMetadata_AreCorrect()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(new SchemaOverviewRequest());

        result.Summary.Should().NotBeNullOrWhiteSpace();
        result.Dialect.Should().Be("SqlServer");
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


    [SkippableFact]

    [Trait("Category", "Integration")]
    public async Task GetSchemaOverview_ViewsExcluded_WhenIncludeViewsFalse()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetSchemaOverviewAsync(
            new SchemaOverviewRequest(IncludeViews: false));

        result.Schemas.SelectMany(s => s.Objects)
            .Should().OnlyContain(o => o.Kind == DatabaseObjectKind.Table);
    }
}

/// <summary>xUnit fixture for SQL Server integration tests.</summary>
public sealed class SqlServerFixture : IAsyncLifetime, IDatabaseFixture
{
    private const string DefaultConnectionString =
        "Server=localhost,1434;Database=dblens_demo;User Id=dblens_reader;Password=ReaderPassword123!;TrustServerCertificate=true";

    // sa, for asserting that a write-capable login is refused.
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("DBLENS_TEST_SS_CONNECTION") ?? DefaultConnectionString;

    private static string AdminConnectionString => SqlServerAdmin.ConnectionString;

    public Task RunWorkloadAsync() => IndexWorkload.OnSqlServerAsync(ConnectionString, "users");

    public DbLens.Core.Abstractions.IDatabaseInspector CreateAdminInspector(bool readOnlyLoginOnLocalhost = false) =>
        new SqlServerInspector(AdminConnectionString, NullLogger<SqlServerInspector>.Instance)
        {
            ReadOnlyLoginOnLocalhost = readOnlyLoginOnLocalhost,
        };

    public bool IsReachable { get; private set; }
    public SqlServerInspector? Inspector { get; private set; }

    DbLens.Core.Abstractions.IDatabaseInspector? IDatabaseFixture.Inspector => Inspector;

    public string UserSchema => "dbo";

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
