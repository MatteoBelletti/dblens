using DbLens.Docs.Inventory;
using FluentAssertions;

namespace DbLens.Tests.Docs;

/// <summary>
/// Components, events, links and flows against what was written by hand (eShop, from grep and reading the code) or found by a
/// separate regex pass (EventualShop): the inventory must find exactly those, no more and no fewer.
/// </summary>
public sealed class OverviewFixtureTests
{
    private static Dictionary<string, HashSet<string>> Truth(string name)
    {
        Dictionary<string, HashSet<string>> truth = [];
        string? section = null;
        foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Docs", $"{name}.truth.txt")).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
            if (line.StartsWith('[')) truth[section = line.Trim('[', ']')] = [];
            else truth[section!].Add(line);
        return truth;
    }

    private static string[] Split(string list) => list.Split(", ", StringSplitOptions.RemoveEmptyEntries);

    private static Dictionary<string, HashSet<string>> Found(ProjectInventory inventory)
    {
        var components = inventory.Of(ItemKind.Component).ToList();
        var links = inventory.Of(ItemKind.Link).ToList();
        string Edge(InventoryItem l) => $"{l.Attributes["from"]}->{l.Attributes["to"]} {l.Attributes["via"]}";
        return new Dictionary<string, HashSet<string>>
        {
            ["macros"] = [.. components.Where(c => c.Attributes["role"] != "shared-library").Select(c => c.Name)],
            ["shared"] = [.. components.Where(c => c.Attributes["role"] == "shared-library").Select(c => c.Name)],
            ["private"] = [.. components.Where(c => c.Attributes["role"] != "shared-library").SelectMany(c => Split(c.Attributes["libraries"]).Select(l => $"{c.Name}={l["project:".Length..]}"))],
            ["producers"] = [.. inventory.Of(ItemKind.Event).SelectMany(e => Split(e.Attributes["producers"]).Select(p => $"{e.Name}@{p}"))],
            ["subscriptions"] = [.. inventory.Of(ItemKind.Event).SelectMany(e => Split(e.Attributes["consumers"]).Select(c => $"{e.Name}@{c}"))],
            ["eventedges"] = [.. links.Where(l => l.Attributes["kind"] == "event").Select(Edge)],
            ["sync"] = [.. links.Where(l => l.Attributes["kind"] == "call").Select(Edge)],
            ["data"] = [.. links.Where(l => l.Attributes["kind"] == "data").Select(Edge)],
        };
    }

    private static void ShouldMatchTheTruth(ProjectInventory inventory, string name, params string[] sections)
    {
        var truth = Truth(name);
        var found = Found(inventory);
        foreach (var section in sections)
        {
            found[section].Except(truth[section]).Should().BeEmpty($"{name}: nothing beyond the truth in [{section}]");
            truth[section].Except(found[section]).Should().BeEmpty($"{name}: nothing of the truth missed in [{section}]");
        }
    }

    private static readonly Lazy<ProjectInventory> EShop = new(() => ProjectInventoryEngine.Build(DocsFixtures.EShopFlows()));
    private static readonly Lazy<ProjectInventory> Eventual = new(() => ProjectInventoryEngine.Build(DocsFixtures.EventualShop()));

    [Fact]
    public void EShop_Components_AreTheElevenExecutables_WithTheirPrivateLibraries_AndFiveSharedOnes() =>
        ShouldMatchTheTruth(EShop.Value, "eShopFlows", "macros", "private", "shared");

    [Fact]
    public void EShop_Events_AreProducedAndConsumedWhereTheCodeSays() =>
        ShouldMatchTheTruth(EShop.Value, "eShopFlows", "producers", "subscriptions", "eventedges");

    [Fact]
    public void EShop_Calls_AndHandWrittenSql_AreLinks() =>
        ShouldMatchTheTruth(EShop.Value, "eShopFlows", "sync", "data");

    [Fact]
    public void EShop_TheGracePeriodFlow_IsTheChainOfTheOrderTheDocumentByHandDescribes()
    {
        var hops = EShop.Value.Item("flow:hosted:GracePeriodManagerService").Attributes["hops"].Split(" | ");

        hops.Should().Equal(
            "OrderProcessor -> Ordering.API : GracePeriodConfirmedIntegrationEvent",
            "Ordering.API -> Catalog.API : OrderStatusChangedToAwaitingValidationIntegrationEvent",
            "Ordering.API -> WebApp : OrderStatusChangedToAwaitingValidationIntegrationEvent",
            "Catalog.API -> Ordering.API : OrderStockConfirmedIntegrationEvent",
            "Catalog.API -> Ordering.API : OrderStockRejectedIntegrationEvent",
            "Ordering.API -> PaymentProcessor : OrderStatusChangedToStockConfirmedIntegrationEvent",
            "Ordering.API -> WebApp : OrderStatusChangedToStockConfirmedIntegrationEvent",
            "PaymentProcessor -> Ordering.API : OrderPaymentFailedIntegrationEvent",
            "PaymentProcessor -> Ordering.API : OrderPaymentSucceededIntegrationEvent",
            "Ordering.API -> WebApp : OrderStatusChangedToCancelledIntegrationEvent",
            "Ordering.API -> Catalog.API : OrderStatusChangedToPaidIntegrationEvent",
            "Ordering.API -> WebApp : OrderStatusChangedToPaidIntegrationEvent",
            "Ordering.API -> Webhooks.API : OrderStatusChangedToPaidIntegrationEvent");
    }

    [Theory]
    [InlineData("flow:endpoint:POST /api/orders", "OrderStartedIntegrationEvent, OrderStatusChangedToSubmittedIntegrationEvent")]
    [InlineData("flow:endpoint:PUT /api/orders/cancel", "OrderStatusChangedToCancelledIntegrationEvent")]
    [InlineData("flow:endpoint:PUT /api/orders/ship", "OrderStatusChangedToShippedIntegrationEvent")]
    [InlineData("flow:endpoint:PUT /api/catalog/items", "ProductPriceChangedIntegrationEvent")]
    [InlineData("flow:endpoint:PUT /api/catalog/items/{id:int}", "ProductPriceChangedIntegrationEvent")]
    public void EShop_AnEndpoint_StartsAFlowWithTheEventsItRaises(string flow, string events) =>
        EShop.Value.Item(flow).Attributes["events"].Should().Be(events);

    [Fact]
    public void EShop_OnlyTheEntryPointsThatSetSomethingInMotion_StartAFlow() =>
        EShop.Value.Of(ItemKind.Flow).Should().HaveCount(6);

    [Fact]
    public void EventualShop_ComponentsAreNamedByTheirFolders_AndTheEventsMatchTheRegexPass()
    {
        ShouldMatchTheTruth(Eventual.Value, "EventualShop", "macros", "shared", "producers", "subscriptions", "eventedges");
    }

    [Fact]
    public void EventualShop_ComposeGivesTheInfrastructureOfEachService()
    {
        Eventual.Value.Item("component:Warehouse/Command").Attributes["infrastructure"].Should().Be("mssql (database), rabbitmq (broker)");
        Eventual.Value.Item("component:Warehouse/Query").Attributes["infrastructure"].Should().Be("mongodb (database), rabbitmq (broker)");
    }

    [Fact]
    public void EventualShop_TheEventStoreContextOfEachService_HasItsOwnIdAndSnapshot()
    {
        var contexts = Eventual.Value.Of(ItemKind.Context).ToList();

        contexts.Select(c => c.Id).Should().Equal(
            "context:EventStoreDbContext [src/Services/ShoppingCart/Command/Infrastructure.EventStore]",
            "context:EventStoreDbContext [src/Services/Warehouse/Command/Infrastructure.EventStore]");
        contexts.Select(c => c.Source.File).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void NoSettingOrComposeValue_ReachesTheInventory()
    {
        var json = InventoryJson.Serialize(Eventual.Value) + InventoryJson.Serialize(EShop.Value);

        json.Should().NotContain("MyStrongPassword").And.NotContain("mongoadmin").And.NotContain("RABBITMQ_DEFAULT_PASS");
    }
}
