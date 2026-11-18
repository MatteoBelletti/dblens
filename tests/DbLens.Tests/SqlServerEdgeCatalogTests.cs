using DbLens.Core.Abstractions;
using DbLens.Core.Models;
using DbLens.SqlServer;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbLens.Tests;

public sealed class SqlServerEdgeCatalogTests(SqlServerEdgeFixture fixture)
    : EdgeCatalogTestsBase(fixture), IClassFixture<SqlServerEdgeFixture>
{
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ForeignKeysSharingAName_InDifferentSchemas_StaySeparate()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // SQL Server scopes a constraint name to its schema, so two schemas can each hold "fk_same".
        var details = await Inspector.GetTableDetailsAsync(
            new TableDetailsRequest([$"{Schema}.kid", $"{DottedSchema}.child"]));

        details.Tables[0].ForeignKeysOutgoing.Should().ContainSingle()
            .Which.Should().Match<ForeignKeyDetail>(fk => fk.ConstraintName == "fk_same" && fk.ToTable == "parent");
        details.Tables[1].ForeignKeysOutgoing.Should().ContainSingle()
            .Which.Should().Match<ForeignKeyDetail>(fk => fk.ConstraintName == "fk_same" && fk.ToTable == "tbl.with.dot");
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task IndexedView_ListsItsIndex()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var details = await Inspector.GetTableDetailsAsync(new TableDetailsRequest([$"{Schema}.v_indexed"]));

        details.Tables.Single().Indexes.Should().ContainSingle().Which.Columns.Should().Equal("parent_id");
    }
}

/// <summary>
/// Creates and drops the edge database on the demo SQL Server, with a case-sensitive collation so
/// that names differing only in case can exist.
/// </summary>
public sealed class SqlServerEdgeFixture : IAsyncLifetime, IEdgeFixture
{
    private const string Database = "dblens_edge";

    private static readonly string[] SetupSql =
    [
        "CREATE SCHEMA [edge.dot]",
        "CREATE SCHEMA edge",

        "CREATE TABLE [edge.dot].[tbl.with.dot] (id int PRIMARY KEY, note nvarchar(50))",
        """
        CREATE TABLE [edge.dot].child (
            id int PRIMARY KEY,
            parent_id int CONSTRAINT fk_same REFERENCES [edge.dot].[tbl.with.dot](id) ON DELETE CASCADE)
        """,

        "CREATE TABLE edge.[Twin] (id int PRIMARY KEY, label nvarchar(50))",
        "CREATE TABLE edge.twin (id int PRIMARY KEY, label nvarchar(50))",

        "CREATE TABLE edge.parent (id int PRIMARY KEY)",
        "CREATE TABLE edge.kid (id int PRIMARY KEY, parent_id int CONSTRAINT fk_same REFERENCES edge.parent(id))",
        "CREATE INDEX ix_kid_partial ON edge.kid (parent_id) WHERE parent_id > 0",
        "CREATE VIEW edge.v_kid AS SELECT id, parent_id FROM edge.kid",
        """
        CREATE VIEW edge.v_indexed WITH SCHEMABINDING AS
        SELECT parent_id, COUNT_BIG(*) AS n FROM edge.kid WHERE parent_id IS NOT NULL GROUP BY parent_id
        """,
        "CREATE UNIQUE CLUSTERED INDEX ix_v_indexed ON edge.v_indexed (parent_id)",

        "CREATE TABLE edge.covered (id int PRIMARY KEY, a int, b int)",
        "CREATE INDEX ix_a ON edge.covered (a)",
        "CREATE INDEX ix_a_incl ON edge.covered (a) INCLUDE (b)",
        "CREATE INDEX ix_a_b ON edge.covered (a, b)",

        "CREATE TABLE edge.ordered (id int PRIMARY KEY, a int, b int)",
        "CREATE INDEX ix_ordered ON edge.ordered (a, b)",
        "CREATE INDEX ix_ordered_mixed ON edge.ordered (a, b DESC)",
        "CREATE INDEX ix_ordered_reversed ON edge.ordered (a DESC, b DESC)",

        "CREATE TABLE edge.account (id int PRIMARY KEY, tenant_id int, CONSTRAINT uq_account UNIQUE (tenant_id, id))",
        """
        CREATE TABLE edge.membership (
            id int PRIMARY KEY, tenant_id int,
            account_id int REFERENCES edge.account (id) ON DELETE CASCADE,
            FOREIGN KEY (tenant_id, account_id) REFERENCES edge.account (tenant_id, id))
        """,
        """
        CREATE TABLE edge.approval (
            id int PRIMARY KEY,
            account_id int REFERENCES edge.account (id) ON DELETE CASCADE,
            approved_by int REFERENCES edge.account (id))
        """,

        $"CREATE LOGIN {LimitedLogin} WITH PASSWORD = 'Limited123!', CHECK_POLICY = OFF",
        $"CREATE USER {LimitedLogin} FOR LOGIN {LimitedLogin}",
        $"GRANT SELECT ON SCHEMA::edge TO {LimitedLogin}",
    ];

    private const string LimitedLogin = "dblens_edge_limited";

    private static string EdgeConnectionString =>
        SqlServerAdmin.On(Database);

    private const string DropSql =
        $"IF DB_ID('{Database}') IS NOT NULL BEGIN " +
        $"ALTER DATABASE {Database} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {Database}; END; " +
        $"IF SUSER_ID('{LimitedLogin}') IS NOT NULL DROP LOGIN {LimitedLogin}";

    public bool IsReachable { get; private set; }

    public IDatabaseInspector? Inspector { get; private set; }

    public IDatabaseInspector? LimitedInspector { get; private set; }

    public Task RunWorkloadAsync() => IndexWorkload.OnSqlServerAsync(EdgeConnectionString, "edge.account");

    public async Task InitializeAsync()
    {
        if (!await SqlServerAdmin.IsReachableAsync()) return;

        // From here a failure is a broken fixture, not an unreachable database: it must fail, not skip.
        await SqlServerAdmin.ExecuteAsync(SqlServerAdmin.ConnectionString, DropSql);
        await SqlServerAdmin.ExecuteAsync(SqlServerAdmin.ConnectionString, $"CREATE DATABASE {Database} COLLATE Latin1_General_100_CS_AS");
        foreach (var statement in SetupSql)
            await SqlServerAdmin.ExecuteAsync(EdgeConnectionString, statement);

        Inspector = new SqlServerInspector(EdgeConnectionString, NullLogger<SqlServerInspector>.Instance);
        LimitedInspector = new SqlServerInspector(
            new SqlConnectionStringBuilder(EdgeConnectionString) { UserID = LimitedLogin, Password = "Limited123!" }.ToString(),
            NullLogger<SqlServerInspector>.Instance);
        IsReachable = true;
    }

    public async Task DisposeAsync()
    {
        if (IsReachable)
            await SqlServerAdmin.ExecuteAsync(SqlServerAdmin.ConnectionString, DropSql);
    }

}
