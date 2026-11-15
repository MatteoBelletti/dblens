using DbLens.Core.Models;
using DbLens.Postgres;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for PostgresInspector.GetTableDetailsAsync.
/// Requires the dblens-postgres container to be running (docker-compose up postgres).
/// Skipped automatically when PostgreSQL is not reachable.
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
public sealed class PostgresTableDetailsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture = fixture;

    private bool ShouldSkip => !_fixture.IsReachable;

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_Users_ReturnsTenColumns()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        result.Tables.Should().ContainSingle();
        result.Tables[0].Columns.Should().HaveCount(10);
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_Users_EmailColumn_IsTextNotNullable()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        var email = result.Tables[0].Columns.Single(c => c.Name == "email");
        email.DataType.Should().Be("text");
        email.IsNullable.Should().BeFalse();
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_Users_IdColumn_IsIdentity()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        var id = result.Tables[0].Columns.Single(c => c.Name == "id");
        id.IsIdentity.Should().BeTrue();
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_Users_HasPrimaryKey()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        var t = result.Tables[0];
        t.PrimaryKey.Should().NotBeNull();
        t.PrimaryKey!.Columns.Should().Equal("id");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_Users_HasAtLeastOnePrimaryKeyIndex()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        result.Tables[0].Indexes.Should().Contain(i => i.IsPrimary);
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_Users_HasAtLeastThreeIncomingForeignKeys()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        // orders, addresses, and audit_logs (loose) reference users
        result.Tables[0].ForeignKeysIncoming.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_Users_OutgoingForeignKeysIsEmpty()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        result.Tables[0].ForeignKeysOutgoing.Should().BeEmpty();
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_ForeignKeyOnDelete_IsPopulated()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // orders has a FK to users with ON DELETE NO ACTION (default)
        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["orders"]));

        var fkToUsers = result.Tables[0].ForeignKeysOutgoing
            .Single(fk => fk.ToTable == "users");

        fkToUsers.OnDelete.Should().NotBeNullOrEmpty();
        fkToUsers.OnDelete.Should().BeOneOf(
            "NO ACTION", "CASCADE", "SET NULL", "SET DEFAULT", "RESTRICT");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_Categories_SelfReferentialFk()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["categories"]));

        var t = result.Tables[0];
        // categories.parent_id → categories.id
        t.ForeignKeysOutgoing.Should().ContainSingle(fk =>
            fk.ToTable == "categories" && fk.FromSchema == "public");
        t.ForeignKeysIncoming.Should().ContainSingle(fk =>
            fk.FromTable == "categories");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_NonExistentTable_AppearsInNotFound()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users", "nonexistent_xyz"]));

        result.Tables.Should().ContainSingle();
        result.NotFound.Should().Contain("nonexistent_xyz");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_QualifiedAndUnqualifiedName_ReturnSameTable()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var r1 = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));
        var r2 = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["public.users"]));

        r1.Tables[0].Columns.Count.Should().Be(r2.Tables[0].Columns.Count);
        r1.Tables[0].Indexes.Count.Should().Be(r2.Tables[0].Indexes.Count);
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_MultipleTables_AreAllReturned()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users", "orders"]));

        result.Tables.Should().HaveCount(2);
        result.Tables.Select(t => t.Name).Should().Contain(["users", "orders"]);
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_MultipleTables_PreservesInputOrder()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["orders", "users"]));

        result.Tables[0].Name.Should().Be("orders");
        result.Tables[1].Name.Should().Be("users");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_Columns_AreSortedByOrdinalPosition()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        var positions = result.Tables[0].Columns.Select(c => c.OrdinalPosition).ToList();
        positions.Should().BeInAscendingOrder();
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_Indexes_AreSortedAlphabetically()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        result.Tables[0].Indexes.Select(i => i.Name).Should().BeInAscendingOrder();
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_SummaryAndMetadata_AreCorrect()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        result.Dialect.Should().Be("PostgreSql");
        result.Database.Should().Be("dblens_demo");
        result.GeneratedAt.Should().NotBeNullOrWhiteSpace();
        result.Summary.Should().Contain("1 of 1");
        result.Truncated.Should().BeFalse();
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_IndexDefinition_ContainsCreateIndex()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        result.Tables[0].Indexes
            .Should().OnlyContain(i => i.Definition.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase));
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_Products_HasNumericPriceColumn()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["products"]));

        var price = result.Tables[0].Columns.Single(c => c.Name == "price");
        price.DataType.Should().Contain("numeric");
        price.NumericPrecision.Should().Be(10);
        price.NumericScale.Should().Be(2);
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_Users_UniqueEmailConstraint_ExistsAsIndex()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // email TEXT NOT NULL UNIQUE — creates a unique index
        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        result.Tables[0].Indexes.Should().Contain(i => i.IsUnique && i.Columns.Contains("email"));
    }
}
