using DbLens.Core.Analysis.EfCore;
using DbLens.Docs.Documents;
using DbLens.Docs.Inventory;
using FluentAssertions;
using static DbLens.Tests.Docs.InventoryTestSupport;

namespace DbLens.Tests.Docs;

/// <summary>Which entities an endpoint's code names: a heuristic over names, kept as signals, never as a complete map.</summary>
public sealed class EntityLinkTests
{
    private static EfModel Context(string name, params string[] entities) =>
        new(name, "10.0.0", null, [.. entities.Select(e => new EfEntity(e, null, e[(e.LastIndexOf('.') + 1)..].ToLowerInvariant(), [], ["Id"], [], []))], [], []);

    private static ProjectInventory Solution(string code, params EfModel[] models) => Build(
        models,
        Project("src/Api/Api.csproj", "Microsoft.NET.Sdk.Web"),
        InventoryTestSupport.Code("src/Api/Api.cs", code));

    private static string Touches(ProjectInventory inventory, string id) => inventory.Item(id).Attributes["touches"];

    [Fact]
    public void ALambdaThatNamesAnEntity_TouchesIt_AndTheEntityIsUsedByTheEndpoint()
    {
        var inventory = Solution(
            "public static class A { public static void M(WebApplication app) { app.MapGet(\"/orders/{id}\", (int id, Db db) => db.Orders.Find(id) as Order); } }",
            Context("ShopContext", "Shop.Order", "Shop.Customer"));

        Touches(inventory, "endpoint:GET /orders/{id}").Should().Be("Order");
        inventory.Item("entity:ShopContext.Shop.Order").Attributes["usedBy"].Should().Be("GET /orders/{id}");
        inventory.Item("entity:ShopContext.Shop.Customer").Attributes["usedBy"].Should().BeEmpty();
    }

    [Fact]
    public void AHandlerNamedByAMethod_IsReadWhereverItIsDeclared_WhenOnlyOneHasThatName()
    {
        var inventory = Build(
            [Context("ShopContext", "Shop.Order")],
            Project("src/Api/Api.csproj", "Microsoft.NET.Sdk.Web"),
            InventoryTestSupport.Code("src/Api/Api.cs", "public static class A { public static void M(WebApplication app) { app.MapPost(\"/orders\", Handlers.Create); } }"),
            InventoryTestSupport.Code("src/Api/Handlers.cs", "public static class Handlers { public static Order Create(OrderRequest r) => new Order(); }"));

        Touches(inventory, "endpoint:POST /orders").Should().Be("Order");
    }

    [Fact]
    public void AHandlerNameThatTwoMethodsShare_IsNotGuessed()
    {
        var inventory = Build(
            [Context("ShopContext", "Shop.Order")],
            Project("src/Api/Api.csproj", "Microsoft.NET.Sdk.Web"),
            InventoryTestSupport.Code("src/Api/Api.cs", "public static class A { public static void M(WebApplication app) { app.MapPost(\"/orders\", Create); } }"),
            InventoryTestSupport.Code("src/Api/One.cs", "public static class One { public static Order Create() => null; }"),
            InventoryTestSupport.Code("src/Api/Two.cs", "public static class Two { public static void Create() { } }"));

        Touches(inventory, "endpoint:POST /orders").Should().BeEmpty();
    }

    [Fact]
    public void AControllerAction_TouchesTheEntitiesOfItsSignatureAndBody()
    {
        var inventory = Solution(
            "[ApiController][Route(\"orders\")] public class OrdersController { [HttpGet(\"{id}\")] public Order Get(int id) { return null; } }",
            Context("ShopContext", "Shop.Order"));

        Touches(inventory, "endpoint:GET /orders/{id}").Should().Be("Order");
    }

    [Fact]
    public void AMemberOfSomethingElseWithTheSameName_IsNotAnEntity()
    {
        var inventory = Solution(
            "public static class A { public static void M(WebApplication app) { app.MapGet(\"/x\", (Request r) => r.Order); } }",
            Context("ShopContext", "Shop.Order"));

        Touches(inventory, "endpoint:GET /x").Should().BeEmpty();
    }

    [Fact]
    public void ANameTwoContextsShare_IsListedWithEachContext()
    {
        var inventory = Solution(
            "public static class A { public static void M(WebApplication app) { app.MapGet(\"/x\", (Order o) => o); } }",
            Context("Sales", "Sales.Order"), Context("Archive", "Archive.Order"));

        Touches(inventory, "endpoint:GET /x").Should().Be("Archive.Order, Sales.Order");
    }

    [Fact]
    public void WithoutEntities_ThereAreNoLinksNoNote_AndNoColumn()
    {
        var inventory = Solution("public static class A { public static void M(WebApplication app) { app.MapGet(\"/x\", () => 1); } }");
        var documents = Scaffolder.Scaffold(inventory, new Dictionary<string, string>(), new DocsOptions()).Files;

        Touches(inventory, "endpoint:GET /x").Should().BeEmpty();
        inventory.Coverage.Should().NotContain(c => c.Area == "links");
        documents["docs/project/api.md"].Should().NotContain("Entities named");
    }

    [Fact]
    public void WithLinks_TheDocumentsSayTheyAreInferred_AndTheNoteCountsTheEndpointsWithNone()
    {
        var inventory = Solution(
            "public static class A { public static void M(WebApplication app) { app.MapGet(\"/orders\", (Order o) => o); app.MapGet(\"/ping\", () => 1); } }",
            Context("ShopContext", "Shop.Order"));
        var documents = Scaffolder.Scaffold(inventory, new Dictionary<string, string>(), new DocsOptions()).Files;

        documents["docs/project/api.md"].Should().Contain("Entities named in the handler (inferred)");
        documents["docs/project/data-model.md"].Should().Contain("Named by endpoints (inferred)");
        inventory.Coverage.Single(c => c.Area == "links").Count.Should().Be(1);
    }

    [Fact]
    public void ANewLink_MakesTheEntityItemOutdated_SoTheCheckAsksForAScaffold()
    {
        var before = Solution("public static class A { public static void M(WebApplication app) { app.MapGet(\"/x\", () => 1); } }", Context("ShopContext", "Shop.Order"));
        var documents = Scaffolder.Scaffold(before, new Dictionary<string, string>(), new DocsOptions()).Files;
        var after = Solution("public static class A { public static void M(WebApplication app) { app.MapGet(\"/x\", (Order o) => o); } }", Context("ShopContext", "Shop.Order"));

        DocumentCheck.Check(after, documents, new DocsOptions()).Issues.Where(i => i.Code == "outdated-item").Select(i => i.Id)
            .Should().BeEquivalentTo("endpoint:GET /x", "entity:ShopContext.Shop.Order");
    }
}
