using DbLens.Docs.Documents;
using DbLens.Docs.Inventory;
using FluentAssertions;
using static DbLens.Tests.Docs.InventoryTestSupport;

namespace DbLens.Tests.Docs;

/// <summary>The cases a review of the overview raised, each with the behaviour it asked for.</summary>
public sealed class OverviewReviewTests
{
    private const string Bus = "public abstract record IntegrationEvent;\npublic interface IIntegrationEventHandler<T> { Task Handle(T e); }\n";

    [Fact]
    public void AProjectsHash_DoesNotChangeWithReferencesNoDocumentShows()
    {
        var project = Build(Web("src/A/A.csproj")).Item("project:A");
        var changed = project with { Attributes = new SortedDictionary<string, string>(project.Attributes.ToDictionary(a => a.Key, a => a.Key == "projectReferenceIds" ? "project:Other" : a.Value), StringComparer.Ordinal) };

        ItemHash.Of(changed).Should().Be(ItemHash.Of(project));
    }

    [Fact]
    public void ACallByName_DoesNotLeaveTheComponent_AndACommonNameIsNotACall()
    {
        var inventory = Build(
            Web("src/Basket/Basket.csproj"),
            File("src/Basket/Bus.cs", Bus),
            File("src/Basket/Basket.cs", "public record ItemAddedIntegrationEvent(int Id) : IntegrationEvent;\npublic class Basket { public void Add(int id) { events.Raise(new ItemAddedIntegrationEvent(id)); } public void PutIn(int id) { events.Raise(new ItemAddedIntegrationEvent(id)); } }"),
            Web("src/Shop/Shop.csproj"),
            File("src/Shop/Bus.cs", Bus),
            File("src/Shop/Api.cs", "public static class Api { public static void Map(WebApplication app) { app.MapGet(\"/x\", Show); app.MapGet(\"/y\", Put); }\n static void Show() { dictionary.Add(1, 2); }\n static void Put() { PutIn(3); } }\npublic class Other { public void PutIn(int id) { } }"));

        inventory.Of(ItemKind.Flow).Should().BeEmpty("the Add of another component, and a name every collection has, are not calls into the solution");
    }

    [Fact]
    public void ACallByName_InsideAComponent_IsFollowed_WhenTheNameIsUnique()
    {
        var inventory = Build(
            Web("src/Shop/Shop.csproj"),
            File("src/Shop/Bus.cs", Bus),
            File("src/Shop/Api.cs", "public record PlacedIntegrationEvent(int Id) : IntegrationEvent;\npublic static class Api { public static void Map(WebApplication app) { app.MapPost(\"/p\", Place); }\n static void Place() { Announce(); }\n static void Announce() { events.Raise(new PlacedIntegrationEvent(1)); } }"));

        inventory.Item("flow:endpoint:POST /p").Attributes["events"].Should().Be("PlacedIntegrationEvent");
    }

    [Fact]
    public void AnEventBehindALongPath_IsStillFound_ThroughAShortOne()
    {
        // The long chain is met first by a depth-first walk; the helper must be reached by its short path as well.
        var chain = string.Concat(Enumerable.Range(1, 18).Select(i => $"static void M{i}() {{ {(i < 18 ? $"M{i + 1}();" : "Helper();")} }}\n"));
        var inventory = Build(
            Web("src/Shop/Shop.csproj"),
            File("src/Shop/Bus.cs", Bus),
            File("src/Shop/Api.cs", "public record DeepIntegrationEvent(int Id) : IntegrationEvent;\npublic static class Api { public static void Map(WebApplication app) { app.MapPost(\"/d\", Entry); }\n static void Entry() { Helper(); M1(); }\n"
                                    + chain + "static void Helper() { events.Raise(new DeepIntegrationEvent(1)); } }"));

        inventory.Item("flow:endpoint:POST /d").Attributes["events"].Should().Be("DeepIntegrationEvent");
    }

    [Fact]
    public void AFlowCutAtTheDepthLimit_SaysSo()
    {
        var handlers = string.Concat(Enumerable.Range(0, 14).Select(i => $"public record E{i}IntegrationEvent(int Id) : IntegrationEvent;\npublic class H{i} : IIntegrationEventHandler<E{i}IntegrationEvent> {{ public Task Handle(E{i}IntegrationEvent e) {{ events.Raise(new E{i + 1}IntegrationEvent(1)); return Task.CompletedTask; }} }}\n"))
                       + "public record E14IntegrationEvent(int Id) : IntegrationEvent;\n"
                       + "public static class Setup { public static void Wire(IEventBus bus) { " + string.Concat(Enumerable.Range(0, 14).Select(i => $"bus.AddSubscription<E{i}IntegrationEvent, H{i}>(); ")) + "}\n"
                       + "public static void Map(WebApplication app) { app.MapPost(\"/go\", Go); }\n static void Go() { events.Raise(new E0IntegrationEvent(1)); } }";
        var inventory = Build(Web("src/Chain/Chain.csproj"), File("src/Chain/Bus.cs", Bus), File("src/Chain/Chain.cs", handlers));

        inventory.Coverage.Should().Contain(c => c.Area == "flows" && c.Note.Contains("cut at 12 hops"));
    }

    [Fact]
    public void MergingComponents_KeepsTheUsersOfASharedLibrary_AndTheRoleOfAService()
    {
        SourceFile[] files =
        [
            Web("src/A/A.csproj", "..\\Contracts\\Contracts.csproj", "..\\Common\\Common.csproj"), Web("src/B/B.csproj", "..\\Contracts\\Contracts.csproj", "..\\Common\\Common.csproj"),
            Lib("src/Contracts/Contracts.csproj"), Lib("src/Common/Common.csproj"),
        ];

        var merged = ProjectInventoryEngine.Build(files, [], null, null, new InventoryOptions { Merge = new Dictionary<string, IReadOnlyList<string>> { ["Shared"] = ["Contracts", "Common"] } });

        merged.Item("component:Shared").Attributes.Should().Contain("role", "shared-library").And.Contain("usedBy", "A, B");
    }

    [Fact]
    public void ANameListedUnderTwoMergeTargets_IsAClearArgumentError()
    {
        SourceFile[] files = [Web("src/X/X.csproj"), Web("src/A/A.csproj"), Web("src/B/B.csproj")];
        var options = new InventoryOptions { Merge = new Dictionary<string, IReadOnlyList<string>> { ["A"] = ["X"], ["B"] = ["X"] } };

        new Action(() => ProjectInventoryEngine.Build(files, [], null, null, options)).Should().Throw<ArgumentException>().WithMessage("*X is listed under both A and B*");
    }

    [Fact]
    public void AServiceSpreadOverABaseAndAnOverrideFile_IsOneService_WithTheKindOfTheFileThatHasTheImage()
    {
        var inventory = Build(
            Web("src/Shop/Shop.csproj"),
            File("docker-compose.override.yml", "services:\n  db:\n    environment:\n      A: b\n"),
            File("docker-compose.yml", "services:\n  db:\n    image: postgres:16\n  shop:\n    build: ./src/Shop\n    depends_on: [db]\n"));

        inventory.Item("component:Shop").Attributes["infrastructure"].Should().Be("db (database)");
    }

    [Theory]
    [InlineData("acme/dynatrace-agent:1", "db (service)")]
    [InlineData("mcr.microsoft.com/mssql/server", "db (database)")]
    [InlineData("bitnami/kafka", "db (broker)")]
    public void TheKindOfAService_IsReadFromTheWordsOfItsImage_NotFromSubstrings(string image, string expected)
    {
        var inventory = Build(
            Web("src/Shop/Shop.csproj"),
            File("docker-compose.yml", $"services:\n  db:\n    image: {image}\n  shop:\n    build: ./src/Shop\n    depends_on: [db]\n"));

        inventory.Item("component:Shop").Attributes["infrastructure"].Should().Be(expected);
    }

    [Fact]
    public void AServiceThatInheritsItsDependenciesFromAnAnchor_HasThem()
    {
        var inventory = Build(
            Web("src/Shop/Shop.csproj"),
            File("docker-compose.yml", "x-common: &common\n  depends_on: [rabbitmq]\nservices:\n  rabbitmq:\n    image: rabbitmq:3\n  shop:\n    <<: *common\n    build: ./src/Shop\n"));

        inventory.Item("component:Shop").Attributes["infrastructure"].Should().Be("rabbitmq (broker)");
    }

    [Fact]
    public void AComposeFileNestedFarTooDeep_OrFarTooLarge_IsSkippedNotACrash()
    {
        var deep = "services: " + new string('[', 5000) + new string(']', 5000);
        var large = "services:\n  a:\n    image: x\n" + string.Concat(Enumerable.Repeat("# padding padding padding padding padding padding\n", 12000));

        var inventory = Build(Web("src/Shop/Shop.csproj"), File("docker-compose.yml", deep), File("compose.yml", large));

        inventory.Coverage.Should().Contain(c => c.Area == "components" && c.Note.Contains("YAML") && c.Count == 2);
    }

    [Fact]
    public void SqlOnAGenericSchema_OrOneTwoComponentsShare_IsNotALink()
    {
        var model = new DbLens.Core.Analysis.EfCore.EfModel("A", "10.0.0", "dbo", [], [], []);
        var shared = new DbLens.Core.Analysis.EfCore.EfModel("B", "10.0.0", "sales", [], [], []);
        var again = new DbLens.Core.Analysis.EfCore.EfModel("C", "10.0.0", "sales", [], [], []);
        var inventory = ProjectInventoryEngine.Build(
            [
                Web("src/A/A.csproj"), File("src/A/Migrations/AModelSnapshot.cs", "partial class AModelSnapshot { }"),
                Web("src/B/B.csproj"), File("src/B/Migrations/BModelSnapshot.cs", "partial class BModelSnapshot { }"),
                Web("src/C/C.csproj"), File("src/C/Migrations/CModelSnapshot.cs", "partial class CModelSnapshot { }"),
                Web("src/D/D.csproj"), File("src/D/Repo.cs", "public class Repo { string A = \"SELECT 1 FROM dbo.X\"; string B = \"SELECT 1 FROM sales.Y\"; }"),
            ],
            [model, shared, again], null,
            ["src/A/Migrations/AModelSnapshot.cs", "src/B/Migrations/BModelSnapshot.cs", "src/C/Migrations/CModelSnapshot.cs"]);

        inventory.Of(ItemKind.Link).Should().BeEmpty();
    }

    [Fact]
    public void AProjectThatOnlyMentionsDistributedApplicationInACommentOrATestingBuilder_IsStillAComponent()
    {
        var inventory = Build(
            Web("src/Shop/Shop.csproj"),
            File("src/Shop/Health.cs", "// the AppHost is a DistributedApplication elsewhere\npublic class Health { string T = \"DistributedApplication\"; object B = DistributedApplicationTestingBuilder.CreateAsync(); }"));

        inventory.Of(ItemKind.Component).Select(c => c.Name).Should().Equal("Shop");
    }
}
