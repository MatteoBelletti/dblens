using DbLens.Core.Models;
using DbLens.Postgres;
using DbLens.SqlServer;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbLens.Tests;

/// <summary>
/// Cross-dialect parity tests: verify that PostgreSQL and SQL Server return the same
/// logical structure for the same tables. Allowed differences: column type names
/// (text vs nvarchar) and constraint/index names. Everything else must match.
/// </summary>
public sealed class TableDetailsParityTests(PostgresFixture pg, SqlServerFixture ss) :
    IClassFixture<PostgresFixture>,
    IClassFixture<SqlServerFixture>
{
    private readonly PostgresFixture _pg = pg;
    private readonly SqlServerFixture _ss = ss;

    private bool ShouldSkip => !_pg.IsReachable || !_ss.IsReachable;

    [SkippableFact, Trait("Category", "Integration")]
    public async Task Users_ColumnCount_IsTheSameOnBothDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.GetTableDetailsAsync(new TableDetailsRequest(["users"]));
        var ss = await _ss.Inspector!.GetTableDetailsAsync(new TableDetailsRequest(["users"]));

        pg.Tables[0].Columns.Count.Should().Be(ss.Tables[0].Columns.Count,
            "both dialects must return the same number of columns for users");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task Users_ColumnNames_AreTheSameOnBothDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.GetTableDetailsAsync(new TableDetailsRequest(["users"]));
        var ss = await _ss.Inspector!.GetTableDetailsAsync(new TableDetailsRequest(["users"]));

        var pgNames = pg.Tables[0].Columns.Select(c => c.Name).OrderBy(n => n).ToList();
        var ssNames = ss.Tables[0].Columns.Select(c => c.Name).OrderBy(n => n).ToList();

        pgNames.Should().Equal(ssNames,
            "column names must be identical across dialects");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task Users_Nullability_IsConsistentAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.GetTableDetailsAsync(new TableDetailsRequest(["users"]));
        var ss = await _ss.Inspector!.GetTableDetailsAsync(new TableDetailsRequest(["users"]));

        var pgNullable = pg.Tables[0].Columns
            .OrderBy(c => c.Name)
            .Select(c => (c.Name, c.IsNullable))
            .ToList();
        var ssNullable = ss.Tables[0].Columns
            .OrderBy(c => c.Name)
            .Select(c => (c.Name, c.IsNullable))
            .ToList();

        pgNullable.Should().Equal(ssNullable,
            "nullability must be the same on both dialects for each column");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task Users_IncomingFkCount_IsTheSameOnBothDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.GetTableDetailsAsync(new TableDetailsRequest(["users"]));
        var ss = await _ss.Inspector!.GetTableDetailsAsync(new TableDetailsRequest(["users"]));

        pg.Tables[0].ForeignKeysIncoming.Count.Should()
            .Be(ss.Tables[0].ForeignKeysIncoming.Count,
                "incoming FK count must match across dialects");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task Orders_OutgoingFkCount_IsTheSameOnBothDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.GetTableDetailsAsync(new TableDetailsRequest(["orders"]));
        var ss = await _ss.Inspector!.GetTableDetailsAsync(new TableDetailsRequest(["orders"]));

        pg.Tables[0].ForeignKeysOutgoing.Count.Should()
            .Be(ss.Tables[0].ForeignKeysOutgoing.Count,
                "outgoing FK count must match across dialects");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task MultipleTable_IndexCount_IsConsistentAcrossDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var tables = new[] { "users", "orders", "products" };
        var pg = await _pg.Inspector!.GetTableDetailsAsync(new TableDetailsRequest(tables));
        var ss = await _ss.Inspector!.GetTableDetailsAsync(new TableDetailsRequest(tables));

        // At minimum both should have a PK index for each table
        foreach (var tName in tables)
        {
            var pgT = pg.Tables.Single(t => t.Name == tName);
            var ssT = ss.Tables.Single(t => t.Name == tName);
            pgT.Indexes.Should().Contain(i => i.IsPrimary,
                $"PG: {tName} must have a PK index");
            ssT.Indexes.Should().Contain(i => i.IsPrimary,
                $"SS: {tName} must have a PK index");
        }
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task Users_IdIsIdentity_OnBothDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.GetTableDetailsAsync(new TableDetailsRequest(["users"]));
        var ss = await _ss.Inspector!.GetTableDetailsAsync(new TableDetailsRequest(["users"]));

        pg.Tables[0].Columns.Single(c => c.Name == "id").IsIdentity
            .Should().BeTrue("PG: id must be identity");
        ss.Tables[0].Columns.Single(c => c.Name == "id").IsIdentity
            .Should().BeTrue("SS: id must be identity");
    }

    [SkippableFact, Trait("Category", "Integration")]
    public async Task NotFound_BehavesTheSameOnBothDialects()
    {
        Skip.If(ShouldSkip, TestSkip.Reason);

        var pg = await _pg.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users", "does_not_exist_xyz"]));
        var ss = await _ss.Inspector!.GetTableDetailsAsync(
            new TableDetailsRequest(["users", "does_not_exist_xyz"]));

        pg.NotFound.Should().Contain("does_not_exist_xyz");
        ss.NotFound.Should().Contain("does_not_exist_xyz");
        pg.Tables.Should().ContainSingle();
        ss.Tables.Should().ContainSingle();
    }
}
