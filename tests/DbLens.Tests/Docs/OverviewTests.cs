using DbLens.Core.Analysis.EfCore;
using DbLens.Docs.Documents;
using DbLens.Docs.Inventory;
using FluentAssertions;
using static DbLens.Tests.Docs.InventoryTestSupport;

namespace DbLens.Tests.Docs;

public sealed class OverviewTests
{
    private static string[] Hops(ProjectInventory inventory, string flowId) => inventory.Item(flowId).Attributes["hops"].Split(" | ");

    private const string Bus = """
        public abstract record IntegrationEvent;
        public record OrderPlacedIntegrationEvent(int Id) : IntegrationEvent;
        public record OrderBilledIntegrationEvent(int Id) : IntegrationEvent;
        public record OrderShippedIntegrationEvent(int Id) : IntegrationEvent;
        public interface IIntegrationEventHandler<T> { Task Handle(T e); }
        """;

    // Ordering: an endpoint places an order through a command, an aggregate and a domain event; Billing consumes it and bills; Shipping consumes that.
    internal static ProjectInventory OrderFlow() => Build(
        Web("src/Ordering/Ordering.csproj"),
        File("src/Ordering/Bus.cs", Bus),
        File("src/Ordering/Api.cs", """
            public static class Api
            {
                public static void Map(WebApplication app) { app.MapPost("/orders", Place); }
                static void Place(IMediator mediator) { mediator.Send(new PlaceOrder()); }
            }
            public record PlaceOrder : IRequest;
            public class PlaceOrderHandler : IRequestHandler<PlaceOrder>
            {
                public void Handle(PlaceOrder command) { var order = new Order(); }
            }
            public class Order
            {
                public Order() { Raise(); }
                private void Raise() { AddDomainEvent(new OrderPlacedDomainEvent()); }
            }
            public record OrderPlacedDomainEvent : INotification;
            public class OrderPlacedDomainEventHandler : INotificationHandler<OrderPlacedDomainEvent>
            {
                public void Handle(OrderPlacedDomainEvent e) { events.Add(new OrderPlacedIntegrationEvent(1)); }
            }
            """),
        Web("src/Billing/Billing.csproj"),
        File("src/Billing/Bus.cs", Bus),
        File("src/Billing/Handlers.cs", """
            public class OrderPlacedHandler : IIntegrationEventHandler<OrderPlacedIntegrationEvent>
            {
                public Task Handle(OrderPlacedIntegrationEvent e) { bus.Add(new OrderBilledIntegrationEvent(e.Id)); return Task.CompletedTask; }
            }
            public static class Setup { public static void Wire(IEventBus bus) { bus.AddSubscription<OrderPlacedIntegrationEvent, OrderPlacedHandler>(); } }
            """),
        Web("src/Shipping/Shipping.csproj"),
        File("src/Shipping/Bus.cs", Bus),
        File("src/Shipping/Handlers.cs", """
            public class OrderBilledHandler : IIntegrationEventHandler<OrderBilledIntegrationEvent>
            {
                public Task Handle(OrderBilledIntegrationEvent e) { bus.Add(new OrderShippedIntegrationEvent(e.Id)); return Task.CompletedTask; }
            }
            public static class Setup { public static void Wire(IEventBus bus) { bus.AddSubscription<OrderBilledIntegrationEvent, OrderBilledHandler>(); } }
            """));

    [Fact]
    public void AnEventHasItsProducerAndItsConsumers_AndEachConnectionIsALink()
    {
        var inventory = OrderFlow();

        inventory.Item("event:OrderPlacedIntegrationEvent").Attributes.Should().Contain("producers", "Ordering").And.Contain("consumers", "Billing");
        inventory.Item("event:OrderShippedIntegrationEvent").Reason.Should().Contain("nothing in the solution consumes it");
        inventory.Of(ItemKind.Link).Where(l => l.Attributes["kind"] == "event").Select(l => l.Id).Should().Equal(
            "link:event Billing -> Shipping (OrderBilledIntegrationEvent)", "link:event Ordering -> Billing (OrderPlacedIntegrationEvent)");
    }

    [Fact]
    public void AFlow_FollowsTheCommandTheAggregateAndTheDomainEvent_ThenEachConsumerAcrossTheBus()
    {
        var hops = Hops(OrderFlow(), "flow:endpoint:POST /orders");

        hops.Should().Equal(
            "Ordering -> Billing : OrderPlacedIntegrationEvent",
            "Billing -> Shipping : OrderBilledIntegrationEvent",
            "Shipping -> (no consumer) : OrderShippedIntegrationEvent");
    }

    [Fact]
    public void AnIntegrationEvent_IsNotFollowedToItsHandlers_SoAConsumersEventsAreTheirOwn()
    {
        var inventory = OrderFlow();

        // Billing's handler creates OrderBilled only: what Shipping then creates is Shipping's, not Billing's.
        Hops(inventory, "flow:endpoint:POST /orders").Should().NotContain(h => h.StartsWith("Billing -> (no consumer) : OrderShipped", StringComparison.Ordinal));
        inventory.Item("flow:endpoint:POST /orders").Attributes["components"].Should().Be("Billing, Ordering, Shipping");
    }

    [Fact]
    public void AHandlerThatReachesAnAggregateMethodThatRaisesNothing_LeadsToNoEvent()
    {
        var inventory = Build(
            Web("src/Stock/Stock.csproj"),
            File("src/Stock/Bus.cs", Bus),
            File("src/Stock/Rejected.cs", """
                public record StockRejectedIntegrationEvent(int Id) : IntegrationEvent;
                public record OrderCancelledIntegrationEvent(int Id) : IntegrationEvent;
                public record RejectStock : IRequest;
                public class RejectedHandler : IIntegrationEventHandler<StockRejectedIntegrationEvent>
                {
                    public Task Handle(StockRejectedIntegrationEvent e) { mediator.Send(new RejectStock()); return Task.CompletedTask; }
                }
                public class RejectStockHandler : IRequestHandler<RejectStock>
                {
                    public void Handle(RejectStock command) { order.CancelBecauseStockIsRejected(); }
                }
                public class Order
                {
                    public void CancelBecauseStockIsRejected() { Status = 6; }
                    public void Cancel() { Status = 6; AddDomainEvent(new CancelledDomainEvent()); }
                }
                public record CancelledDomainEvent : INotification;
                public class CancelledDomainEventHandler : INotificationHandler<CancelledDomainEvent>
                {
                    public void Handle(CancelledDomainEvent e) { events.Add(new OrderCancelledIntegrationEvent(1)); }
                }
                public static class Setup
                {
                    public static void Wire(IEventBus bus) { bus.AddSubscription<StockRejectedIntegrationEvent, RejectedHandler>(); }
                    public static void Map(WebApplication app) { app.MapPut("/cancel", CancelIt); app.MapPost("/reject", RejectIt); }
                    static void CancelIt(Order order) { order.Cancel(); }
                    static void RejectIt(IEventBus bus) { bus.Publish(new StockRejectedIntegrationEvent(1)); }
                }
                """));

        inventory.Item("flow:endpoint:PUT /cancel").Attributes["events"].Should().Be("OrderCancelledIntegrationEvent", "the aggregate raises a domain event there");
        inventory.Item("flow:endpoint:POST /reject").Attributes["hops"].Should().Be("Stock -> Stock : StockRejectedIntegrationEvent", "the rejecting method raises nothing, so no cancellation follows");
    }

    [Fact]
    public void AnEventThatIsOnlyConsumed_IsUnresolvedWithItsReason()
    {
        var inventory = Build(
            Web("src/Billing/Billing.csproj"),
            File("src/Billing/Handlers.cs", """
                public record PaymentReceivedIntegrationEvent : IntegrationEvent;
                public class H : IIntegrationEventHandler<PaymentReceivedIntegrationEvent> { public Task Handle(PaymentReceivedIntegrationEvent e) => Task.CompletedTask; }
                public static class S { public static void W(IEventBus b) { b.AddSubscription<PaymentReceivedIntegrationEvent, H>(); } }
                """));

        inventory.Item("event:PaymentReceivedIntegrationEvent").Should().Match<InventoryItem>(e => e.Evidence == ItemEvidence.Unresolved && e.Reason!.Contains("creates it"));
        inventory.Coverage.Should().Contain(c => c.Area == "events");
    }

    [Fact]
    public void MassTransitStyle_ConsumersByBaseClass_AndEventsRaisedWithAGenericCall_AreRead()
    {
        var inventory = Build(
            Lib("src/Contracts/Contracts.csproj"),
            File("src/Contracts/Events.cs", """
                public static class DomainEvent
                {
                    public record CartCreated(Guid Id) : Message, IDomainEvent;
                    public record CartClosed(Guid Id) : Message, IDomainEvent;
                }
                """),
            Csproj("src/Services/Cart/Command/WorkerService/WorkerService.csproj", "Microsoft.NET.Sdk.Worker", "..\\..\\..\\..\\Contracts\\Contracts.csproj"),
            File("src/Services/Cart/Command/WorkerService/Cart.cs", "public class Cart { public void Create() => RaiseEvent<DomainEvent.CartCreated>(1); public void Registered(IServices s) => s.AddScoped<IProjector<DomainEvent.CartClosed>>(); }"),
            Csproj("src/Services/Cart/Query/WorkerService/WorkerService.csproj", "Microsoft.NET.Sdk.Worker", "..\\..\\..\\..\\Contracts\\Contracts.csproj"),
            File("src/Services/Cart/Query/WorkerService/Consumers.cs", "public class CartCreatedConsumer : Consumer<Contracts.DomainEvent.CartCreated> { }\npublic class CartClosedConsumer : IConsumer<DomainEvent.CartClosed> { }"));

        inventory.Item("event:DomainEvent.CartCreated").Attributes.Should().Contain("producers", "Cart/Command").And.Contain("consumers", "Cart/Query");
        inventory.Item("event:DomainEvent.CartClosed").Attributes["producers"].Should().BeEmpty("a registration that names an event is not a producer");
    }

    [Fact]
    public void TheConfiguration_AddsToThePresets_ForAHomeGrownBus()
    {
        SourceFile[] files =
        [
            Web("src/A/A.csproj"), Web("src/B/B.csproj"),
            File("src/A/E.cs", "public record Ping : IMyEvent;\npublic class Pinger { public void Go() => Emit<Ping>(); }"),
            File("src/B/C.cs", "public class PingHandler : Consumer<Ping> { }"),
        ];

        ProjectInventoryEngine.Build(files, []).Of(ItemKind.Event).Should().BeEmpty("IMyEvent is not a known base");

        var configured = ProjectInventoryEngine.Build(files, [], null, null, new InventoryOptions { EventBases = ["IMyEvent"], ProducingMethods = ["Emit"] });

        configured.Item("event:Ping").Attributes.Should().Contain("producers", "A").And.Contain("consumers", "B");
    }

    [Fact]
    public void AnExecutableAndItsPrivateLibraries_AreOneComponent_AndASharedLibraryIsListedWithItsUsers()
    {
        var inventory = Build(
            Web("src/Orders/Orders.csproj", "..\\Orders.Domain\\Orders.Domain.csproj", "..\\Shared\\Shared.csproj"),
            Lib("src/Orders.Domain/Orders.Domain.csproj"),
            Web("src/Billing/Billing.csproj", "..\\Shared\\Shared.csproj"),
            Lib("src/Shared/Shared.csproj"),
            Web("tests/Orders.Tests/Orders.Tests.csproj", "..\\..\\src\\Orders\\Orders.csproj"));

        var orders = inventory.Item("component:Orders");
        orders.Attributes.Should().Contain("role", "service").And.Contain("libraries", "project:Orders.Domain");
        var shared = inventory.Item("component:Shared");
        shared.Attributes.Should().Contain("role", "shared-library").And.Contain("usedBy", "Billing, Orders");
        inventory.Of(ItemKind.Component).Select(c => c.Name).Should().Equal("Billing", "Orders", "Shared");
    }

    [Fact]
    public void ExecutablesThatShareAName_AreNamedByTheFoldersThatTellThemApart_AndCanBeMerged()
    {
        SourceFile[] files =
        [
            Csproj("src/Services/Account/Command/WorkerService/WorkerService.csproj", "Microsoft.NET.Sdk.Worker"),
            Csproj("src/Services/Account/Query/WorkerService/WorkerService.csproj", "Microsoft.NET.Sdk.Worker"),
            Csproj("src/Services/Order/Command/WorkerService/WorkerService.csproj", "Microsoft.NET.Sdk.Worker"),
        ];

        Build(files).Of(ItemKind.Component).Select(c => c.Name).Should().Equal("Account/Command", "Account/Query", "Order/Command");

        var merged = ProjectInventoryEngine.Build(files, [], null, null, new InventoryOptions { Merge = new Dictionary<string, IReadOnlyList<string>> { ["Account"] = ["Account/Command", "Account/Query"] } });

        merged.Of(ItemKind.Component).Select(c => c.Name).Should().Equal("Account", "Order/Command");
        merged.Item("component:Account").Attributes["executables"].Should().Contain("Account/Command").And.Contain("Account/Query");
    }

    [Fact]
    public void ProjectReferences_AreResolvedToIds_WhenNamesRepeat()
    {
        var inventory = Build(
            Web("src/A/Api/Api.csproj", "..\\Domain\\Domain.csproj"),
            Lib("src/A/Domain/Domain.csproj"),
            Web("src/B/Api/Api.csproj", "..\\Domain\\Domain.csproj"),
            Lib("src/B/Domain/Domain.csproj"));

        inventory.Item("project:Api [src/A/Api]").Attributes["projectReferenceIds"].Should().Be("project:Domain [src/A/Domain]");
        inventory.Item("project:Api [src/B/Api]").Attributes["projectReferenceIds"].Should().Be("project:Domain [src/B/Domain]");
    }

    [Fact]
    public void AnAspireAppHost_NamesTheServices_InBothForms_AndWhatReferencesWhat()
    {
        var inventory = Build(
            Lib("src/AppHost/AppHost.csproj"),
            File("src/AppHost/AppHost.cs", """
                var builder = DistributedApplication.CreateBuilder(args);
                var bus = builder.AddRabbitMQ("eventbus");
                var orders = builder.AddDotnetProject("orders-api", "../Orders");
                builder.AddProject<Projects.Web_Shop>("shop").WithReference(orders).WithReference(bus);
                orders.WithReference(bus);
                """),
            Web("src/Orders/Orders.csproj"),
            Web("src/Web.Shop/Web.Shop.csproj"));

        inventory.Of(ItemKind.Link).Select(l => l.Id).Should().Equal("link:reference Web.Shop -> Orders (AppHost)");
        inventory.Item("component:Orders").Attributes.Should().Contain("infrastructure", "eventbus (broker)");
        inventory.Item("component:Web.Shop").Attributes.Should().Contain("infrastructure", "eventbus (broker)");
        inventory.Of(ItemKind.Component).Select(c => c.Name).Should().NotContain("AppHost", "the orchestrator is not a component");
    }

    [Fact]
    public void ACallWhoseUrlNamesAService_IsALink_AndAnUnknownHostIsNot()
    {
        var inventory = Build(
            Lib("src/AppHost/AppHost.csproj"),
            File("src/AppHost/AppHost.cs", "var b = DistributedApplication.CreateBuilder(args);\nb.AddDotnetProject(\"orders-api\", \"../Orders\");\nb.AddDotnetProject(\"shop\", \"../Shop\");"),
            Web("src/Orders/Orders.csproj"),
            Web("src/Shop/Shop.csproj"),
            File("src/Shop/Setup.cs", "public static class S { public static void M(IServices s) { s.AddHttpClient<OrderingClient>(o => o.BaseAddress = new(\"https+http://orders-api\")); s.AddHttpClient<Other>(o => o.BaseAddress = new(\"https://api.example.com\")); } }"));

        inventory.Of(ItemKind.Link).Where(l => l.Attributes["kind"] == "call").Select(l => (l.Id, l.Evidence)).Should().Equal(("link:call Shop -> Orders (AddHttpClient)", ItemEvidence.Inferred));
    }

    [Fact]
    public void ComposeServices_BuiltFromAProject_GiveDependenciesAndInfrastructure_AndNoValueIsKept()
    {
        var inventory = Build(
            Web("src/Orders/Orders.csproj"),
            Web("src/Shop/Shop.csproj"),
            File("docker-compose.yaml", """
                services:
                  rabbit:
                    image: rabbitmq:3-management
                    environment:
                      RABBITMQ_DEFAULT_PASS: "S3cret-Rabbit-Pass"
                  orders:
                    build:
                      context: .
                      dockerfile: ./src/Orders/Dockerfile
                    depends_on:
                      rabbit:
                        condition: service_healthy
                    environment:
                      SA_PASSWORD: "Sup3r-Secret-Db-Pass"
                  shop:
                    build: ./src/Shop
                    depends_on: [orders]
                    environment:
                      - ORDERS_URL=http://orders:8080
                      - TOKEN=amqp://guest:Another-Secret@rabbit:5672
                """));

        inventory.Of(ItemKind.Link).Select(l => l.Id).Should().Equal(
            "link:call Shop -> Orders (compose environment)", "link:depends Shop -> Orders (compose)");
        inventory.Item("component:Orders").Attributes.Should().Contain("infrastructure", "rabbit (broker)");
        InventoryJson.Serialize(inventory).Should().NotContain("S3cret-Rabbit-Pass").And.NotContain("Sup3r-Secret-Db-Pass").And.NotContain("Another-Secret");
    }

    [Fact]
    public void AComposeFileThatIsNotYaml_IsSkipped_AndSaysSo()
    {
        var inventory = Build(Web("src/Orders/Orders.csproj"), File("docker-compose.yml", "services: [unclosed"));

        inventory.Coverage.Should().Contain(c => c.Area == "components" && c.Note.Contains("not valid YAML"));
    }

    [Fact]
    public void HandWrittenSql_ThatNamesAnotherComponentsSchema_IsADataLink()
    {
        EfModel model = new("ShopContext", "10.0.0", "ordering", [new EfEntity("Order", null, "orders", [], ["Id"], [], [])], [], []);
        var inventory = ProjectInventoryEngine.Build(
            [
                Web("src/Orders/Orders.csproj"),
                File("src/Orders/Migrations/ShopContextModelSnapshot.cs", "partial class ShopContextModelSnapshot { }"),
                Csproj("src/Worker/Worker.csproj", "Microsoft.NET.Sdk.Worker"),
                File("src/Worker/Repo.cs", "public class Repo { string Sql = \"SELECT \\\"Id\\\" FROM ordering.orders WHERE x = 1\"; }"),
            ],
            [model], null, ["src/Orders/Migrations/ShopContextModelSnapshot.cs"]);

        var link = inventory.Of(ItemKind.Link).Single();

        link.Id.Should().Be("link:data Worker -> Orders (ordering.orders)");
        link.Evidence.Should().Be(ItemEvidence.Inferred);
    }

    [Fact]
    public void TheConfigurationFile_AddsToTheDefaults_AndAMalformedOneIsAnArgumentError()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dblens-docs-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            System.IO.File.WriteAllText(Path.Combine(root, DocsConfig.FileName), "{ \"events\": { \"bases\": [\"IMyEvent\"] }, \"components\": { \"Account\": [\"Account/Command\", \"Account/Query\"] } }");
            var options = DocsConfig.LoadInventory(root);

            options.EventBases.Should().Contain(["IntegrationEvent", "IEvent", "IDomainEvent", "IMyEvent"]);
            options.Merge["Account"].Should().Equal("Account/Command", "Account/Query");

            foreach (var bad in new[] { "{ \"events\": 3 }", "{ \"events\": { \"bases\": \"x\" } }", "{ \"components\": { \"A\": 1 } }", "[]" })
            {
                System.IO.File.WriteAllText(Path.Combine(root, DocsConfig.FileName), bad);
                new Action(() => DocsConfig.LoadInventory(root)).Should().Throw<ArgumentException>(bad);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheOverviewDocument_HoldsTheComponentsAndTheFlows_AndNoRegionForAnEventOrALink()
    {
        var inventory = OrderFlow();
        var documents = Scaffolder.Scaffold(inventory, new Dictionary<string, string>(), new DocsOptions()).Files;
        var overview = documents["docs/project/overview.md"];

        overview.Should().Contain("dblens:item component:Ordering").And.Contain("dblens:item flow:endpoint:POST /orders").And.Contain("```mermaid\nsequenceDiagram")
            .And.Contain("dblens:generated graph:components").And.Contain("dblens:generated table:events");
        string.Concat(documents.Values).Should().NotContain("dblens:item event:").And.NotContain("dblens:item link:").And.NotContain("dblens:item detail:");
    }
}
