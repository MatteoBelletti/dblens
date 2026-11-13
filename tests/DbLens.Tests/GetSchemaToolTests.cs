using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using DbLens.Mcp.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DbLens.Tests;

public sealed class GetSchemaToolTests
{
    private static DatabaseSchemaOverview MakeOverview(
        IReadOnlyList<SchemaInfo>? schemas = null) =>
        new(
            Summary: "Found 3 tables and 1 view across 2 schemas. Total approximate rows: 100. Total size on disk: 256.0 KB.",
            Dialect: "PostgreSql",
            Database: "test_db",
            GeneratedAt: "2026-01-01T00:00:00.0000000Z",
            Schemas: schemas ?? [
                new SchemaInfo(
                    Name: "app",
                    TableCount: 2,
                    ViewCount: 1,
                    Objects: [
                        new DatabaseObject("orders", DatabaseObjectKind.Table, 80, 6, true, 2, 0, 2, 32768, "32.0 KB", null),
                        new DatabaseObject("users", DatabaseObjectKind.Table, 20, 10, true, 0, 3, 1, 16384, "16.0 KB", "User accounts"),
                        new DatabaseObject("v_active_users", DatabaseObjectKind.View, null, 10, false, 0, 0, 0, 0, "0 B", null),
                    ]),
                new SchemaInfo(
                    Name: "audit",
                    TableCount: 1,
                    ViewCount: 0,
                    Objects: [
                        new DatabaseObject("logs", DatabaseObjectKind.Table, 0, 4, true, 1, 0, 1, 8192, "8.0 KB", null),
                    ]),
            ]);

    private static GetSchemaTool MakeTool(
        Func<SchemaOverviewRequest, DatabaseSchemaOverview>? handler = null,
        Action<DatabaseDialect>? onKeyedResolve = null,
        Action<DatabaseDialect, string>? onFactoryCreate = null) =>
        new(
            new StubKeyedServiceProvider(handler ?? (_ => MakeOverview()), onKeyedResolve),
            new StubFactory(handler ?? (_ => MakeOverview()), onFactoryCreate));

    // --- request forwarding ---

    [Fact]
    public async Task GetSchema_InvokesInspectorWithCorrectRequest()
    {
        var captured = (SchemaOverviewRequest?)null;
        var tool = MakeTool(req => { captured = req; return MakeOverview(); });

        await tool.GetSchema(schemaFilter: "app", namePattern: "user%", includeViews: false);

        captured!.SchemaFilter.Should().Be("app");
        captured.NamePattern.Should().Be("user%");
        captured.IncludeViews.Should().BeFalse();
        captured.IncludeSystemSchemas.Should().BeFalse();
    }

    [Fact]
    public async Task GetSchema_DefaultParams_PassesDefaultsToInspector()
    {
        var captured = (SchemaOverviewRequest?)null;
        var tool = MakeTool(req => { captured = req; return MakeOverview(); });

        await tool.GetSchema();

        captured!.SchemaFilter.Should().BeNull();
        captured.NamePattern.Should().BeNull();
        captured.IncludeViews.Should().BeTrue();
        captured.IncludeSystemSchemas.Should().BeFalse();
    }

    // --- dialect routing ---

    [Fact]
    public async Task GetSchema_DefaultDialect_IsPostgreSql()
    {
        DatabaseDialect? resolved = null;
        var tool = MakeTool(onKeyedResolve: d => resolved = d);

        await tool.GetSchema();

        resolved.Should().Be(DatabaseDialect.PostgreSql);
    }

    [Fact]
    public async Task GetSchema_SqlServerDialect_ResolvesCorrectInspector()
    {
        DatabaseDialect? resolved = null;
        var tool = MakeTool(onKeyedResolve: d => resolved = d);

        await tool.GetSchema(dialect: DatabaseDialect.SqlServer);

        resolved.Should().Be(DatabaseDialect.SqlServer);
    }

    // --- connection string routing ---

    [Fact]
    public async Task GetSchema_NoConnectionString_UsesKeyedService()
    {
        var usedKeyed = false;
        var usedFactory = false;
        var tool = MakeTool(
            onKeyedResolve: _ => usedKeyed = true,
            onFactoryCreate: (_, _) => usedFactory = true);

        await tool.GetSchema();

        usedKeyed.Should().BeTrue();
        usedFactory.Should().BeFalse();
    }

    [Fact]
    public async Task GetSchema_WithConnectionString_UsesFactory()
    {
        var usedKeyed = false;
        var usedFactory = false;
        var tool = MakeTool(
            onKeyedResolve: _ => usedKeyed = true,
            onFactoryCreate: (_, _) => usedFactory = true);

        await tool.GetSchema(connectionString: "Host=custom;Database=mydb");

        usedFactory.Should().BeTrue();
        usedKeyed.Should().BeFalse();
    }

    [Fact]
    public async Task GetSchema_WithConnectionString_PassesDialectAndStringToFactory()
    {
        DatabaseDialect? capturedDialect = null;
        string? capturedCs = null;
        var tool = MakeTool(onFactoryCreate: (d, cs) => { capturedDialect = d; capturedCs = cs; });

        await tool.GetSchema(
            dialect: DatabaseDialect.SqlServer,
            connectionString: "Server=myserver;Database=prod");

        capturedDialect.Should().Be(DatabaseDialect.SqlServer);
        capturedCs.Should().Be("Server=myserver;Database=prod");
    }

    // --- response shape ---

    [Fact]
    public async Task GetSchema_ReturnedOverview_HasCorrectSummary()
    {
        var expected = MakeOverview();
        var result = await MakeTool(_ => expected).GetSchema();
        result.Summary.Should().Be(expected.Summary);
    }

    [Fact]
    public async Task GetSchema_Schemas_AreSortedAlphabetically()
    {
        var result = await MakeTool().GetSchema();
        result.Schemas.Select(s => s.Name).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task GetSchema_ObjectsWithinSchema_AreSortedAlphabetically()
    {
        var result = await MakeTool().GetSchema();
        foreach (var schema in result.Schemas)
            schema.Objects.Select(o => o.Name).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task GetSchema_SchemaInfo_CountsMatchObjects()
    {
        var result = await MakeTool().GetSchema();
        foreach (var schema in result.Schemas)
        {
            schema.TableCount.Should().Be(schema.Objects.Count(o => o.Kind == DatabaseObjectKind.Table));
            schema.ViewCount.Should().Be(schema.Objects.Count(o => o.Kind == DatabaseObjectKind.View));
        }
    }

    [Fact]
    public async Task GetSchema_ViewObject_HasNullApproximateRowCount()
    {
        var result = await MakeTool().GetSchema();
        var view = result.Schemas.SelectMany(s => s.Objects).First(o => o.Kind == DatabaseObjectKind.View);
        view.ApproximateRowCount.Should().BeNull();
    }

    [Fact]
    public async Task GetSchema_AllObjectFields_ArePresent()
    {
        var result = await MakeTool().GetSchema();
        foreach (var obj in result.Schemas.SelectMany(s => s.Objects))
        {
            obj.Name.Should().NotBeNullOrEmpty();
            obj.TotalSizeHuman.Should().NotBeNullOrEmpty();
            obj.ColumnCount.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    // --- stubs ---

    // Implements both IServiceProvider and IKeyedServiceProvider, matching what the real DI container does.
    private sealed class StubKeyedServiceProvider(
        Func<SchemaOverviewRequest, DatabaseSchemaOverview> handler,
        Action<DatabaseDialect>? onResolve = null) : IServiceProvider, IKeyedServiceProvider
    {
        public object? GetService(Type serviceType) => null;

        public object? GetKeyedService(Type serviceType, object? serviceKey)
        {
            if (serviceKey is DatabaseDialect d) onResolve?.Invoke(d);
            return new StubInspector(handler);
        }

        public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
        {
            if (serviceKey is DatabaseDialect d) onResolve?.Invoke(d);
            return new StubInspector(handler);
        }
    }

    private sealed class StubFactory(
        Func<SchemaOverviewRequest, DatabaseSchemaOverview> handler,
        Action<DatabaseDialect, string>? onCreate = null) : IDatabaseInspectorFactory
    {
        public IDatabaseInspector Create(DatabaseDialect dialect, string connectionString)
        {
            onCreate?.Invoke(dialect, connectionString);
            return new StubInspector(handler);
        }
    }

    private sealed class StubInspector(Func<SchemaOverviewRequest, DatabaseSchemaOverview> handler)
        : IDatabaseInspector
    {
        public DatabaseDialect Dialect => DatabaseDialect.PostgreSql;
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DatabaseSchemaOverview> GetSchemaOverviewAsync(
            SchemaOverviewRequest request, CancellationToken ct = default) =>
            Task.FromResult(handler(request));
        public Task<TableDetailsResult> GetTableDetailsAsync(
            TableDetailsRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DbLens.Core.Analysis.Indexes.AnalyzeIndexesResult> AnalyzeIndexesAsync(
            DbLens.Core.Analysis.Indexes.AnalyzeIndexesRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DbLens.Core.Analysis.Gdpr.ScanGdprResult> ScanGdprAsync(
            DbLens.Core.Analysis.Gdpr.ScanGdprRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DbLens.Core.Analysis.Cascade.CheckCascadePathsResult> CheckCascadePathsAsync(
            DbLens.Core.Analysis.Cascade.CheckCascadePathsRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DbLens.Core.Analysis.Denormalization.AnalyzeDenormalizationResult> AnalyzeDenormalizationAsync(
            DbLens.Core.Analysis.Denormalization.AnalyzeDenormalizationRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DbLens.Core.Analysis.Fragmentation.AnalyzeFragmentationResult> AnalyzeFragmentationAsync(
            DbLens.Core.Analysis.Fragmentation.AnalyzeFragmentationRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DbLens.Core.Analysis.QueryPlan.ExplainQueryResult> ExplainQueryAsync(
            DbLens.Core.Analysis.QueryPlan.ExplainQueryRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DbLens.Core.Analysis.Relationships.InferRelationshipsResult> InferRelationshipsAsync(
            DbLens.Core.Analysis.Relationships.InferRelationshipsRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<DbLens.Core.Analysis.Migration.CheckMigrationSafetyResult> CheckMigrationSafetyAsync(
            DbLens.Core.Analysis.Migration.CheckMigrationSafetyRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
