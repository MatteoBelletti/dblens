using DbLens.Core.Models;
using DbLens.SqlServer;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbLens.Tests;

/// <summary>
/// Integration tests for SqlServerInspector.GetTableDetailsAsync.
/// Requires the dblens-sqlserver container to be running (docker-compose up sqlserver).
/// Skipped automatically when SQL Server is not reachable.
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
public sealed class SqlServerTableDetailsTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;
    public SqlServerTableDetailsTests(SqlServerFixture fixture) => _fixture = fixture;
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
    public async Task GetTableDetails_Users_EmailColumn_IsNvarcharNotNullable()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        var email = result.Tables[0].Columns.Single(c => c.Name == "email");
        email.DataType.Should().Be("nvarchar");
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
    public async Task GetTableDetails_Users_HasPrimaryKeyIndex()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        result.Tables[0].Indexes.Should().Contain(i => i.IsPrimary);
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_Users_HasAtLeastTwoIncomingForeignKeys()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

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
    public async Task GetTableDetails_ForeignKeyOnDelete_IsNormalizedString()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["orders"]));

        var fkToUsers = result.Tables[0].ForeignKeysOutgoing
            .Single(fk => fk.ToTable == "users");

        fkToUsers.OnDelete.Should().NotContain("_",
            "on_delete should replace underscores: NO_ACTION → NO ACTION");
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
        t.ForeignKeysOutgoing.Should().ContainSingle(fk => fk.ToTable == "categories");
        t.ForeignKeysIncoming.Should().ContainSingle(fk => fk.FromTable == "categories");
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
            new TableDetailsRequest(["dbo.users"]));

        r1.Tables[0].Columns.Count.Should().Be(r2.Tables[0].Columns.Count);
        r1.Tables[0].Indexes.Count.Should().Be(r2.Tables[0].Indexes.Count);
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
    public async Task GetTableDetails_SummaryAndMetadata_AreCorrect()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        result.Dialect.Should().Be("SqlServer");
        result.Database.Should().Be("dblens_demo");
        result.GeneratedAt.Should().NotBeNullOrWhiteSpace();
        result.Summary.Should().Contain("1 of 1");
        result.Truncated.Should().BeFalse();
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_Products_HasDecimalPriceColumn()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["products"]));

        var price = result.Tables[0].Columns.Single(c => c.Name == "price");
        price.DataType.Should().BeOneOf("decimal", "numeric");
        price.NumericPrecision.Should().Be(10);
        price.NumericScale.Should().Be(2);
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task GetTableDetails_UniqueConstraints_AreInUniqueConstraintsNotIndexes()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        // users.email has a UNIQUE constraint in SQL Server schema
        var result = await _fixture.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users"]));

        var t = result.Tables[0];
        // email unique constraint goes to uniqueConstraints, not to indexes
        t.UniqueConstraints.Should().Contain(uc => uc.Columns.Contains("email"));
        t.Indexes.Should().NotContain(i => !i.IsPrimary && i.IsUnique && i.Columns.Contains("email"),
            "unique constraint indexes must not appear in the indexes list");
    }
}
