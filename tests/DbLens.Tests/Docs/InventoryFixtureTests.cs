using DbLens.Docs.Inventory;
using FluentAssertions;
using static DbLens.Tests.Docs.InventoryTestSupport;

namespace DbLens.Tests.Docs;

/// <summary>The inventory of two real solutions: a trimmed dotnet/eShop and this repository's DemoShop sample.</summary>
public sealed class InventoryFixtureTests
{
    private static ProjectInventory EShop => ProjectInventoryEngine.Build(DocsFixtures.EShop());

    [Fact]
    public void EShop_HasEveryKindOfItem_WithTheCountsTheSpikeMeasured()
    {
        var inventory = EShop;

        inventory.Of(ItemKind.Project).Should().HaveCount(28);
        inventory.Of(ItemKind.Entity).Should().HaveCount(19);
        inventory.Of(ItemKind.HostedService).Select(i => i.Name).Should().BeEquivalentTo("GracePeriodManagerService", "MigrationHostedService<TContext>", "RabbitMQEventBus");
        inventory.Of(ItemKind.HttpClient).Select(i => i.Id).Should().Equal(
            "http:CatalogService [HybridApp]", "http:CatalogService [WebApp]", "http:OrderingService", "http:WebhooksClient");
        inventory.Of(ItemKind.Config).Select(i => i.Id).Should().Contain(
            ["config:ConnectionStrings:CatalogDB", "config:Identity:Url", "config:BackgroundTaskOptions:GracePeriodTime"]);
    }

    [Fact]
    public void EShop_Endpoints_ResolveEveryRouteWrittenInCode_AndNameWhatDoesNot()
    {
        var endpoints = EShop.Of(ItemKind.Endpoint).ToList();

        endpoints.Should().HaveCount(45);
        endpoints.Count(e => e.Evidence == ItemEvidence.Extracted).Should().Be(31);
        endpoints.Where(e => e.Evidence == ItemEvidence.Unresolved).Should().HaveCount(14)
            .And.OnlyContain(e => e.Reason == "conventional routing: no [Route] and no template" && e.Source.File.StartsWith("src/Identity.API/Quickstart/"));
        endpoints.Select(e => e.Id).Should().Contain([
            "endpoint:GET /api/catalog/items [GetAllItemsV1]", "endpoint:GET /api/catalog/items [GetAllItems]",
            "endpoint:GET /api/orders/{orderId:int}", "endpoint:DELETE /api/webhooks/{id:int}", "endpoint:OPTIONS /check", "endpoint:POST /logout"]);
        endpoints.Select(e => e.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EShop_Coverage_NamesWhatWasNotInventoried()
    {
        var coverage = EShop.Coverage;

        coverage.Should().Contain(c => c.Area == "endpoints" && c.Count == 14);
        coverage.Should().Contain(c => c.Note.Contains("MapForwarder x1") && c.Note.Contains("MapGrpcService x1") && c.Note.Contains("MapRazorComponents x2"));
        coverage.Should().Contain(c => c.Area == "http clients" && c.Count == 1);
        coverage.Should().Contain(c => c.Area == "source" && c.Note.Contains("test folders"));
    }

    [Fact]
    public void EShop_CentralPackageVersions_AreResolved_AndProjectsKnowTheirKind()
    {
        var inventory = EShop;

        inventory.Coverage.Should().NotContain(c => c.Area == "projects", "every version and reference of eShop resolves");
        inventory.Item("project:Catalog.API").Attributes["outputTypeFrom"].Should().Be("sdk");
        inventory.Item("project:Catalog.API").Attributes["packages"].Should().NotContain("@,").And.Contain("Asp.Versioning.Http@");
        inventory.Item("project:Catalog.API").Attributes["projectReferences"].Should().Contain("eShop.ServiceDefaults");
    }

    [Fact]
    public void NoSettingsValue_ReachesTheInventory()
    {
        var json = InventoryJson.Serialize(EShop);

        json.Should().NotContain("yourWeak").And.NotContain("Password=");
        System.IO.File.ReadAllText(Path.Combine(DocsFixtures.EShopFolder, "src/Ordering.API/appsettings.Development.json")).Should().Contain("yourWeak", "the fixture does hold a placeholder password to keep out");
    }

    [Fact]
    public void DemoShop_GivesItsEntitiesPerContext_AndNamesTheViewItCannotRead()
    {
        var inventory = ProjectInventoryEngine.Build(DocsFixtures.DemoShop());

        inventory.Of(ItemKind.Entity).Select(i => i.Attributes["context"]).Distinct().Should().BeEquivalentTo("ShopPostgresContext", "ShopSqlServerContext");
        inventory.Item("entity:ShopPostgresContext.DemoShop.EfCore.Order").Attributes.Should().Contain("table", "shop.orders").And.Contain("key", "Id");
        inventory.Item("entity:ShopPostgresContext.DemoShop.EfCore.CardPayment").Attributes.Should().Contain("sharesTableWith", "DemoShop.EfCore.Payment");
        inventory.Item("entity:ShopPostgresContext.DemoShop.EfCore.Order").Source.File.Should().EndWith("Migrations/PostgreSql/ShopPostgresContextModelSnapshot.cs");
        inventory.Coverage.Should().Contain(c => c.Area == "data model" && c.Note.Contains("order_summaries"));
        inventory.Item("project:DemoShop.EfCore").Attributes["packages"].Should().Contain("Microsoft.EntityFrameworkCore.SqlServer");
        inventory.Coverage.Should().Contain(c => c.Area == "projects" && c.Note.Contains("MSBuild property"), "the sample takes its versions from properties");
    }
}
