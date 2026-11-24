using DbLens.Docs.Inventory;
using FluentAssertions;
using static DbLens.Tests.Docs.InventoryTestSupport;

namespace DbLens.Tests.Docs;

/// <summary>An endpoint resolves only when its route is written as text; everything else is kept, with the reason.</summary>
public sealed class EndpointInventoryTests
{
    private static IReadOnlyList<InventoryItem> Endpoints(params string[] sources) =>
        [.. Build(sources.Select((s, i) => Code($"src/Api/File{i}.cs", s)).ToArray()).Of(ItemKind.Endpoint)];

    [Fact]
    public void MinimalApi_WithLiterals_ResolvesThroughGroupsAndVersionedBuilders()
    {
        var endpoints = Endpoints("""
            public static class OrdersApi
            {
                public static IEndpointRouteBuilder MapOrders(this IEndpointRouteBuilder app)
                {
                    var v = app.NewVersionedApi("Orders");
                    var api = v.MapGroup("api/orders").HasApiVersion(1.0);
                    api.MapGet("/", List);
                    api.MapGet("{id:int}", Get);
                    api.MapPost("/draft", Draft).RequireAuthorization();
                    return app;
                }
            }
            """, """
            var app = builder.Build();
            app.MapOrders();
            """);

        endpoints.Select(e => e.Id).Should().Equal("endpoint:GET /api/orders", "endpoint:GET /api/orders/{id:int}", "endpoint:POST /api/orders/draft");
        endpoints.Should().OnlyContain(e => e.Evidence == ItemEvidence.Extracted && e.Reason == null);
        endpoints[0].Attributes["handler"].Should().Be("List");
    }

    [Fact]
    public void ExtensionCalledOnAGroup_TakesThePrefixOfItsCallSites()
    {
        var endpoints = Endpoints("""
            public static class Api
            {
                public static void MapItems(this IEndpointRouteBuilder app) => app.MapGet("items", Items);
            }
            """, """
            var app = builder.Build();
            var v1 = app.MapGroup("/v1");
            v1.MapItems();
            """);

        endpoints.Should().ContainSingle().Which.Id.Should().Be("endpoint:GET /v1/items");
    }

    [Fact]
    public void ExtensionCalledWithDifferentPrefixes_IsUnresolved_WithItsReason()
    {
        var endpoints = Endpoints("""
            public static class Api
            {
                public static void MapItems(this IEndpointRouteBuilder app) => app.MapGet("items", Items);
            }
            """, """
            var app = builder.Build();
            app.MapGroup("/a").MapItems();
            app.MapGroup("/b").MapItems();
            """);

        var item = endpoints.Should().ContainSingle().Subject;
        item.Evidence.Should().Be(ItemEvidence.Unresolved);
        item.Reason.Should().Contain("MapItems is called on groups with different prefixes");
        item.Id.Should().Be("endpoint:GET ?@src/Api/File0.cs [Items]");
    }

    [Fact]
    public void ConstantsOfTheSameFile_Resolve_ButAnotherClassesConstantDoesNot()
    {
        var endpoints = Endpoints("""
            public static class Api
            {
                private const string Base = "api/things";
                public static void Map(IEndpointRouteBuilder app)
                {
                    app.MapGet(Base + "/{id}", One);
                    app.MapGet(Routes.Other, Two);
                    app.MapGet($"{Base}/x", Three);
                }
            }
            """);

        endpoints.Single(e => e.Attributes["handler"] == "One").Id.Should().Be("endpoint:GET /api/things/{id}");
        endpoints.Single(e => e.Attributes["handler"] == "Two").Reason.Should().Contain("not a literal or a constant of the same file");
        endpoints.Single(e => e.Attributes["handler"] == "Three").Evidence.Should().Be(ItemEvidence.Unresolved, "an interpolation with a value is not text");
    }

    [Fact]
    public void MapMethods_GivesOneEndpointPerVerb()
    {
        var endpoints = Endpoints("""
            public static class Api
            {
                public static void Map(IEndpointRouteBuilder app) =>
                    app.MapMethods("/check", [HttpMethods.Options, "head"], Check);
            }
            """);

        endpoints.Select(e => e.Id).Should().Equal("endpoint:HEAD /check", "endpoint:OPTIONS /check");
    }

    [Fact]
    public void TwoVersionsOfTheSameRoute_AreToldApartByHandler_AndTheSameHandlerByOrdinal()
    {
        var endpoints = Endpoints("""
            public static class Api
            {
                public static void Map(IEndpointRouteBuilder app)
                {
                    var v1 = app.MapGroup("/api");
                    var v2 = app.MapGroup("/api");
                    v1.MapGet("/items", ListV1);
                    v2.MapGet("/items", List);
                    v2.MapPost("/x", Create);
                    v1.MapPost("/x", Create);
                    app.MapGet("/only", () => "one");
                }
            }
            """);

        endpoints.Select(e => e.Id).Should().Equal(
            "endpoint:GET /api/items [ListV1]", "endpoint:GET /api/items [List]",
            "endpoint:GET /only", "endpoint:POST /api/x [Create] #1", "endpoint:POST /api/x [Create] #2");
    }

    [Fact]
    public void UnresolvedEndpoints_KeepTheirIdWhenAnotherActionIsAddedToTheFile()
    {
        const string one = """
            public class AController : Controller
            {
                [HttpGet] public IActionResult Index() => View();
            }
            """;
        const string two = """
            public class AController : Controller
            {
                [HttpGet] public IActionResult Index() => View();
                [HttpGet] public IActionResult About() => View();
            }
            """;

        var before = Endpoints(one).Single().Id;
        var after = Endpoints(two).Select(e => e.Id);

        after.Should().Contain(before).And.HaveCount(2);
        before.Should().Be("endpoint:GET ?@src/Api/File0.cs [AController.Index]");
    }

    [Fact]
    public void Controllers_ResolveRouteTokens_AbsoluteTemplates_AndMethodLevelRoutes()
    {
        var endpoints = Endpoints("""
            [ApiController]
            [Route("api/[controller]")]
            public class OrdersController : ControllerBase
            {
                [HttpGet] public IActionResult List() => Ok();
                [HttpGet("{id:int}")] public IActionResult Get(int id) => Ok();
                [HttpPost("[action]")] public IActionResult Import() => Ok();
                [HttpGet("/health/orders")] public IActionResult Health() => Ok();
                [HttpDelete][Route("{id}/archive")] public IActionResult Archive(int id) => Ok();
                [NonAction] public IActionResult Helper() => Ok();
                [HttpGet(Routes.Other)] public IActionResult Computed() => Ok();
                private IActionResult Hidden() => Ok();
            }
            """);

        endpoints.Where(e => e.Evidence == ItemEvidence.Extracted).Select(e => e.Id).Should().BeEquivalentTo(
            "endpoint:GET /api/Orders", "endpoint:GET /api/Orders/{id:int}", "endpoint:POST /api/Orders/Import",
            "endpoint:GET /health/orders", "endpoint:DELETE /api/Orders/{id}/archive");
        endpoints.Single(e => e.Evidence == ItemEvidence.Unresolved).Reason.Should().Be("the route template is not written as text");
        endpoints.Should().HaveCount(6, "NonAction and non-public methods are not endpoints");
    }

    [Fact]
    public void ConventionalRouting_IsUnresolved_NeverInventedAsASlash()
    {
        var endpoints = Endpoints("""
            public class HomeController : Controller
            {
                [HttpGet] public IActionResult Index() => View();
            }
            """);

        var item = endpoints.Should().ContainSingle().Subject;
        item.Evidence.Should().Be(ItemEvidence.Unresolved);
        item.Reason.Should().Be("conventional routing: no [Route] and no template");
        item.Attributes["route"].Should().BeEmpty();
    }

    [Fact]
    public void CallsThatDeclareSomethingElse_AreInCoverage_NotInTheItems()
    {
        var inventory = Build(Code("src/Api/Program.cs", """
            var app = builder.Build();
            app.MapGrpcService<BasketService>();
            app.MapForwarder("/img/{id}", "http://catalog", "/pic/{id}");
            app.MapDefaultEndpoints();
            app.MapOrderToBasket();
            public static class Local { public static void MapOrderToBasket(this IEndpointRouteBuilder b) { } }
            """));

        inventory.Of(ItemKind.Endpoint).Should().BeEmpty();
        inventory.Coverage.Should().Contain(c => c.Area == "endpoints" && c.Note.Contains("MapForwarder x1, MapGrpcService x1") && !c.Note.Contains("MapOrderToBasket"));
    }

    [Fact]
    public void CodeUnderTestFolders_IsNotRead_AndSaysSo()
    {
        var inventory = Build(
            Code("src/Api/Api.cs", "public static class A { public static void M(IEndpointRouteBuilder app) => app.MapGet(\"/real\", H); }"),
            Code("tests/Api.Tests/Fake.cs", "public static class T { public static void M(IEndpointRouteBuilder app) => app.MapGet(\"/fake\", H); }"),
            Code("src/Web.FunctionalTests/Fake2.cs", "public static class T2 { public static void M(IEndpointRouteBuilder app) => app.MapGet(\"/fake2\", H); }"));

        inventory.Of(ItemKind.Endpoint).Should().ContainSingle().Which.Id.Should().Be("endpoint:GET /real");
        inventory.Coverage.Should().Contain(c => c.Area == "source" && c.Count == 2);
    }
}
