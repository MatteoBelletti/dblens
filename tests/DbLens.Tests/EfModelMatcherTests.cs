using DbLens.Core.Analysis.EfCore;
using FluentAssertions;
using static DbLens.Tests.TestSchema;

namespace DbLens.Tests;

public sealed class EfModelMatcherTests
{
    private static EfProperty Prop(string name, string column, bool nullable = false) =>
        new(name, "long", column, "bigint", nullable);

    private static EfEntity Entity(string name, string table, string? schema, params EfProperty[] properties) =>
        new(name, schema, table, properties, ["Id"], [], []);

    private static EfModel Model(string? defaultSchema, params EfEntity[] entities) =>
        new("ShopContext", "10.0.0", defaultSchema, entities, [], []);

    [Fact]
    public void EntitiesFindTheirTables_AndPropertiesTheirColumns()
    {
        var model = Model("shop", Entity("Order", "orders", null, Prop("Id", "id"), Prop("UserId", "user_id"), Prop("Gone", "gone")));
        var orders = Table("orders", [Col("id", "bigint"), Col("user_id", "bigint")], schema: "shop");

        var match = EfModelMatcher.Match(model, [orders, Table("audit", [Col("id", "bigint")], schema: "shop")], "public");

        var table = match.Tables.Single();
        table.Qualified.Should().Be("shop.orders");
        table.Table.Should().BeSameAs(orders);
        table.Columns.Single(c => c.Property.Name == "UserId").Column!.Name.Should().Be("user_id");
        table.Columns.Single(c => c.Property.Name == "Gone").Column.Should().BeNull();
        match.UnmappedTables.Select(t => t.Name).Should().Equal("audit");
    }

    [Fact]
    public void WithoutASchema_TheModelDefaultThenTheDatabaseDefaultApplies()
    {
        var model = Model(null, Entity("Order", "orders", null, Prop("Id", "id")));

        EfModelMatcher.Match(model, [Table("orders", [Col("id", "bigint")])], "public")
            .Tables.Single().Table.Should().NotBeNull("the entity has no schema and neither has the model: public");
    }

    [Fact]
    public void ANameDifferingOnlyInCase_StillMatches_WhenItIsTheOnlyOne()
    {
        var model = Model("dbo", Entity("Order", "Orders", null, Prop("Id", "Id")));

        var match = EfModelMatcher.Match(model, [Table("orders", [Col("id", "bigint")], schema: "dbo")], "dbo");

        match.Tables.Single().Columns.Single().Column.Should().NotBeNull();
        match.Ambiguities.Should().BeEmpty();
    }

    [Fact]
    public void ANameMatchingSeveralByCase_IsReported_NotGuessed()
    {
        var model = Model("public", Entity("Order", "Orders", null, Prop("Id", "id")));

        var match = EfModelMatcher.Match(model,
            [Table("orders", [Col("id", "bigint")]), Table("ORDERS", [Col("id", "bigint")])], "public");

        match.Tables.Single().Table.Should().BeNull();
        match.Ambiguities.Should().ContainSingle(a => a.Contains("public.Orders") && a.Contains("differ only in case"));
    }

    [Fact]
    public void AnOwnedType_IsMatchedInItsOwnersTable()
    {
        var user = Entity("User", "users", "shop", Prop("Id", "id"));
        var address = new EfEntity("Address", "shop", "users", [Prop("City", "home_city", nullable: true)], ["Id"], [], [], SharesTableWith: "User");

        var match = EfModelMatcher.Match(Model("shop", user, address),
            [Table("users", [Col("id", "bigint"), Col("home_city")], schema: "shop")], "public");

        var table = match.Tables.Single();
        table.Owner.Name.Should().Be("User");
        table.Columns.Should().HaveCount(2).And.OnlyContain(c => c.Column != null);
        EfModelMatcher.TableOf(match, "Address").Should().BeSameAs(table);
        EfModelMatcher.ColumnsOf(table, "Address", ["City"]).Should().Equal("home_city");
    }

    [Fact]
    public void APropertyNamedLikeTheOwnersOne_IsTheDeclaringEntitys()
    {
        var user = Entity("User", "users", "shop", Prop("Id", "id"), Prop("City", "city"));
        var address = new EfEntity("Address", "shop", "users", [Prop("City", "home_city", nullable: true)], ["Id"], [], [], SharesTableWith: "User");

        var table = EfModelMatcher.Match(Model("shop", user, address),
            [Table("users", [Col("id", "bigint"), Col("city"), Col("home_city")], schema: "shop")], "public").Tables.Single();

        EfModelMatcher.ColumnsOf(table, "Address", ["City"]).Should().Equal("home_city");
        EfModelMatcher.ColumnsOf(table, "User", ["City"]).Should().Equal("city");
        EfModelMatcher.ColumnsOf(table, "Address", ["Id"]).Should().Equal(["id"], "an owned type's key is its owner's");
    }
}
