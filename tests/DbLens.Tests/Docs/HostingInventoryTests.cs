using DbLens.Docs.Inventory;
using FluentAssertions;
using static DbLens.Tests.Docs.InventoryTestSupport;

namespace DbLens.Tests.Docs;

/// <summary>Configuration keys, external HTTP clients and background services.</summary>
public sealed class HostingInventoryTests
{
    private static ProjectInventory Code(string body, params SourceFile[] more) =>
        Build([InventoryTestSupport.Code("src/App/Program.cs", body), .. more]);

    // ── Configuration ───────────────────────────────────────────────────────────

    [Fact]
    public void Configuration_IsTheKeysTheCodeReads_WithSectionsCombined()
    {
        var inventory = Code("""
            var port = builder.Configuration.GetValue<int>("Server:Port");
            var section = configuration.GetSection("Identity").GetSection("Scopes");
            var url = configuration["Identity:Url"];
            var cs = config.GetConnectionString("Orders");
            var name = _configuration.GetRequiredSection("Mail")["From"];
            builder.Services.Configure<Options>(builder.Configuration.GetSection("Options"));
            var other = somethingElse["NotConfig"];
            """);

        inventory.Of(ItemKind.Config).Select(i => i.Id).Should().Equal(
            "config:ConnectionStrings:Orders", "config:Identity", "config:Identity:Scopes", "config:Identity:Url", "config:Mail", "config:Mail:From",
            "config:Options", "config:Server:Port");
    }

    [Fact]
    public void AKeyReadInSeveralPlaces_IsOneItem_WithTheOthersListed()
    {
        var inventory = Code("var a = configuration[\"Same:Key\"];", InventoryTestSupport.Code("src/App/Other.cs", "class O { void M() { var b = Configuration[\"same:key\"]; } }"));

        var item = inventory.Of(ItemKind.Config).Should().ContainSingle().Subject;
        item.Source.File.Should().Be("src/App/Other.cs", "the primary place is the first by path");
        item.AlsoAt.Should().ContainSingle().Which.File.Should().Be("src/App/Program.cs");
    }

    [Fact]
    public void AKeyThatIsNotText_IsCounted_NotInvented()
    {
        var inventory = Code("var x = configuration[$\"Prefix:{name}\"]; var y = configuration.GetValue<string>(KeyFor(name)); var z = configuration[\"Real:Key\"];");

        inventory.Of(ItemKind.Config).Should().ContainSingle().Which.Id.Should().Be("config:Real:Key");
        inventory.Coverage.Should().Contain(c => c.Area == "configuration" && c.Count == 2);
    }

    [Fact]
    public void SettingsFiles_GiveTheirKeys_AndNeverTheirValues()
    {
        var inventory = Build(
            Project("src/App/App.csproj"),
            File("src/App/appsettings.json", """
                {
                  // a comment
                  "ConnectionStrings": { "Main": "Host=db;Password=hunter2" },
                  "Servers": [ { "Name": "a" }, { "Name": "b" } ],
                  "Flags": [ true, false ],
                  "Mail": { "From": "ops@example.com", },
                }
                """),
            File("src/App/appsettings.Development.json", "{ \"Mail\": { \"From\": \"dev@example.com\" } }"),
            File("src/App/notsettings.json", "{ \"Ignored\": 1 }"));

        var config = inventory.Of(ItemKind.Config).ToList();
        config.Select(i => i.Id).Should().Equal(
            "config:ConnectionStrings:Main", "config:Flags:0", "config:Flags:1", "config:Mail:From", "config:Servers:0:Name", "config:Servers:1:Name");
        var from = config.Single(i => i.Id == "config:Mail:From");
        from.Source.File.Should().Be("src/App/appsettings.Development.json", "the first by path is the primary place");
        from.AlsoAt.Should().ContainSingle().Which.File.Should().Be("src/App/appsettings.json");
        config.Single(i => i.Id == "config:ConnectionStrings:Main").Source.Line.Should().Be(3);
        Serialize(inventory).Should().NotContain("hunter2").And.NotContain("example.com");
    }

    private static string Serialize(ProjectInventory inventory) => InventoryJson.Serialize(inventory);

    // ── HTTP clients ────────────────────────────────────────────────────────────

    [Fact]
    public void HttpClients_AreNamedByTheirTypeOrTheirName_WithTheBaseAddressWhenWritten()
    {
        var inventory = Code("""
            services.AddHttpClient<CatalogClient>(o => o.BaseAddress = new("https://catalog.internal"));
            services.AddHttpClient<IOrders, OrdersClient>().ConfigureHttpClient(c => c.BaseAddress = new Uri(configuration["Orders:Url"]));
            services.AddHttpClient("payments", c => c.BaseAddress = new Uri(PaymentUrl));
            services.AddHttpClient();
            services.AddHttpClient(NameFor(x));
            """);

        var clients = inventory.Of(ItemKind.HttpClient).ToDictionary(i => i.Name);
        clients["CatalogClient"].Attributes.Should().Contain("baseAddress", "https://catalog.internal").And.Contain("baseAddressFrom", "code");
        clients["IOrders"].Attributes.Should().Contain("baseAddress", "configuration key Orders:Url").And.Contain("baseAddressFrom", "configuration");
        clients["payments"].Attributes.Should().Contain("baseAddressFrom", "none");
        clients["(default)"].Evidence.Should().Be(ItemEvidence.Extracted);
        clients.Values.Single(c => c.Evidence == ItemEvidence.Unresolved).Reason.Should().Be("the client name is not written as text");
        inventory.Coverage.Should().Contain(c => c.Area == "http clients" && c.Count == 3);
    }

    [Fact]
    public void ThePrefixedNameOfAClient_RegisteredInTwoProjects_IsToldApartByProject()
    {
        var inventory = Build(
            Project("src/Web/Web.csproj"), Project("src/Mobile/Mobile.csproj"),
            InventoryTestSupport.Code("src/Web/Ext.cs", "class E { void M() { services.AddHttpClient<CatalogService>(o => o.BaseAddress = new(\"http://a\")); } }"),
            InventoryTestSupport.Code("src/Mobile/Ext.cs", "class E { void M() { services.AddHttpClient<CatalogService>(o => o.BaseAddress = new(\"http://b\")); } }"));

        inventory.Of(ItemKind.HttpClient).Select(i => i.Id).Should().Equal("http:CatalogService [Mobile]", "http:CatalogService [Web]");
    }

    // ── Background services ─────────────────────────────────────────────────────

    [Fact]
    public void BackgroundServices_AreFoundDirectlyAndThroughOtherClassesOfTheSolution()
    {
        var inventory = Code("""
            public class Worker : BackgroundService { }
            public abstract class BaseJob : BackgroundService { }
            public class NightlyJob : BaseJob { }
            public class Bus : IEventBus, IHostedService { }
            public class Migrator<TContext> : BackgroundService where TContext : DbContext { }
            public class NotHosted : IDisposable { }
            public class Unknown : SomeLibraryBase { }
            services.AddHostedService<Worker>();
            services.AddHostedService(sp => new Migrator<MyContext>(sp));
            """);

        var hosted = inventory.Of(ItemKind.HostedService).ToDictionary(i => i.Name);
        hosted.Keys.Should().BeEquivalentTo("Worker", "BaseJob", "NightlyJob", "Bus", "Migrator<TContext>");
        hosted["Worker"].Attributes.Should().Contain("registeredWithAddHostedService", "yes");
        hosted["Migrator<TContext>"].Attributes.Should().Contain("registeredWithAddHostedService", "yes");
        hosted["NightlyJob"].Attributes.Should().Contain("registeredWithAddHostedService", "no").And.Contain("bases", "BaseJob");
        hosted["BaseJob"].Attributes.Should().Contain("abstract", "yes");
    }

    [Fact]
    public void ACycleOfBaseClasses_DoesNotLoop()
    {
        var inventory = Code("public class A : B { } public class B : A { }");

        inventory.Of(ItemKind.HostedService).Should().BeEmpty();
    }
}
