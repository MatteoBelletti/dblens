using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using DbLens.Mcp.Infrastructure;
using DbLens.Mcp.Tools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DbLens.Tests;

public sealed class GetTableDetailsToolTests
{
    private static TableDetailsResult MakeResult(IReadOnlyList<TableDetail>? tables = null) =>
        new(
            Summary: "Retrieved details for 1 of 1 requested tables. Found 2 columns, 1 indexes, 0 foreign keys.",
            Dialect: "PostgreSql",
            Database: "test_db",
            GeneratedAt: "2026-01-01T00:00:00.0000000Z",
            Truncated: false,
            NotFound: [],
            Tables: tables ?? [
                new TableDetail(
                    Schema: "public",
                    Name: "users",
                    Kind: "table",
                    Comment: null,
                    Columns: [
                        new ColumnDetail("id", 1, "bigint", "bigint", false, "nextval('users_id_seq'::regclass)",
                            null, 64, 0, true, null),
                        new ColumnDetail("email", 2, "text", "text", false, null,
                            null, null, null, false, null),
                    ],
                    PrimaryKey: new PrimaryKeyInfo("users_pkey", ["id"]),
                    Indexes: [
                        new IndexDetail("users_pkey", ["id"], true, true, "btree",
                            "CREATE UNIQUE INDEX users_pkey ON public.users USING btree (id)",
                            false, null),
                    ],
                    ForeignKeysOutgoing: [],
                    ForeignKeysIncoming: [],
                    CheckConstraints: [],
                    UniqueConstraints: []
                )
            ]
        );

    private static GetTableDetailsTool MakeTool(
        Func<TableDetailsRequest, TableDetailsResult>? handler = null,
        Action<DatabaseDialect>? onKeyedResolve = null,
        Action<DatabaseDialect, string>? onFactoryCreate = null) =>
        new(
            new StubKeyedServiceProvider(handler ?? (_ => MakeResult()), onKeyedResolve),
            new StubFactory(handler ?? (_ => MakeResult()), onFactoryCreate));

    // --- input validation ---

    [Fact]
    public async Task GetTableDetails_EmptyArray_ThrowsArgumentException()
    {
        var tool = MakeTool();
        await tool.Invoking(t => t.GetTableDetails([]))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*empty*");
    }

    [Fact]
    public async Task GetTableDetails_TwentyOneElements_ThrowsArgumentException()
    {
        var tool = MakeTool();
        var tables = Enumerable.Range(1, 21).Select(i => $"t{i}").ToArray();
        await tool.Invoking(t => t.GetTableDetails(tables))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*20*");
    }

    [Fact]
    public async Task GetTableDetails_TwentyElements_DoesNotThrow()
    {
        var tool = MakeTool();
        var tables = Enumerable.Range(1, 20).Select(i => $"t{i}").ToArray();
        await tool.Invoking(t => t.GetTableDetails(tables)).Should().NotThrowAsync();
    }

    [Fact]
    public async Task GetTableDetails_EmptyStringEntry_ThrowsArgumentException()
    {
        var tool = MakeTool();
        await tool.Invoking(t => t.GetTableDetails(["users", "   ", "orders"]))
            .Should().ThrowAsync<ArgumentException>()
            .WithMessage("*tables[1]*");
    }

    // --- request forwarding ---

    [Fact]
    public async Task GetTableDetails_ForwardsTablesAndDefaultSchema()
    {
        TableDetailsRequest? captured = null;
        var tool = MakeTool(req => { captured = req; return MakeResult(); });

        await tool.GetTableDetails(["users", "orders"], defaultSchema: "app");

        captured!.Tables.Should().Equal("users", "orders");
        captured.DefaultSchema.Should().Be("app");
    }

    [Fact]
    public async Task GetTableDetails_NullDefaultSchema_ForwardsNull()
    {
        TableDetailsRequest? captured = null;
        var tool = MakeTool(req => { captured = req; return MakeResult(); });

        await tool.GetTableDetails(["users"]);

        captured!.DefaultSchema.Should().BeNull();
    }

    // --- dialect routing ---

    [Fact]
    public async Task GetTableDetails_DefaultDialect_IsPostgreSql()
    {
        DatabaseDialect? resolved = null;
        var tool = MakeTool(onKeyedResolve: d => resolved = d);

        await tool.GetTableDetails(["users"]);

        resolved.Should().Be(DatabaseDialect.PostgreSql);
    }

    [Fact]
    public async Task GetTableDetails_SqlServerDialect_ResolvesCorrectInspector()
    {
        DatabaseDialect? resolved = null;
        var tool = MakeTool(onKeyedResolve: d => resolved = d);

        await tool.GetTableDetails(["users"], dialect: DatabaseDialect.SqlServer);

        resolved.Should().Be(DatabaseDialect.SqlServer);
    }

    // --- connection string routing ---

    [Fact]
    public async Task GetTableDetails_NoConnectionString_UsesKeyedService()
    {
        var usedKeyed = false;
        var usedFactory = false;
        var tool = MakeTool(
            onKeyedResolve: _ => usedKeyed = true,
            onFactoryCreate: (_, _) => usedFactory = true);

        await tool.GetTableDetails(["users"]);

        usedKeyed.Should().BeTrue();
        usedFactory.Should().BeFalse();
    }

    [Fact]
    public async Task GetTableDetails_WithConnectionString_UsesFactory()
    {
        var usedFactory = false;
        var tool = MakeTool(onFactoryCreate: (_, _) => usedFactory = true);

        await tool.GetTableDetails(["users"], connectionString: "Host=custom;Database=mydb");

        usedFactory.Should().BeTrue();
    }

    // --- notFound propagation ---

    [Fact]
    public async Task GetTableDetails_NotFoundList_IsPassedThrough()
    {
        var resultWithNotFound = MakeResult() with { NotFound = ["nonexistent"] };
        var tool = MakeTool(_ => resultWithNotFound);

        var result = await tool.GetTableDetails(["users", "nonexistent"]);

        result.NotFound.Should().Contain("nonexistent");
    }

    // --- response shape ---

    [Fact]
    public async Task GetTableDetails_AllArrayFields_AreNeverNull()
    {
        var result = await MakeTool().GetTableDetails(["users"]);

        result.NotFound.Should().NotBeNull();
        result.Tables.Should().NotBeNull();

        foreach (var t in result.Tables)
        {
            t.Columns.Should().NotBeNull();
            t.Indexes.Should().NotBeNull();
            t.ForeignKeysOutgoing.Should().NotBeNull();
            t.ForeignKeysIncoming.Should().NotBeNull();
            t.CheckConstraints.Should().NotBeNull();
            t.UniqueConstraints.Should().NotBeNull();
        }
    }

    // --- stubs ---

    private sealed class StubKeyedServiceProvider(
        Func<TableDetailsRequest, TableDetailsResult> handler,
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
        Func<TableDetailsRequest, TableDetailsResult> handler,
        Action<DatabaseDialect, string>? onCreate = null) : IDatabaseInspectorFactory
    {
        public IDatabaseInspector Create(DatabaseDialect dialect, string connectionString)
        {
            onCreate?.Invoke(dialect, connectionString);
            return new StubInspector(handler);
        }
    }

    private sealed class StubInspector(Func<TableDetailsRequest, TableDetailsResult> handler)
        : IDatabaseInspector
    {
        public DatabaseDialect Dialect => DatabaseDialect.PostgreSql;
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DatabaseSchemaOverview> GetSchemaOverviewAsync(
            SchemaOverviewRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<TableDetailsResult> GetTableDetailsAsync(
            TableDetailsRequest request, CancellationToken ct = default) =>
            Task.FromResult(handler(request));
    }
}
