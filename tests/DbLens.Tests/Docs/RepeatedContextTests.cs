using DbLens.Core.Analysis.EfCore;
using DbLens.Docs.Inventory;
using FluentAssertions;
using static DbLens.Tests.Docs.InventoryTestSupport;

namespace DbLens.Tests.Docs;

/// <summary>One DbContext name in several services is common (an event store per service): it must not collide or crash.</summary>
public sealed class RepeatedContextTests
{
    private static EfModel Store(params string[] entities) =>
        new("EventStoreDbContext", "10.0.0", null, [.. entities.Select(e => new EfEntity(e, null, e.ToLowerInvariant(), [], ["Id"], [], []))], [], []);

    private static SourceFile Snapshot(string folder, int blanks) =>
        File($"{folder}/Migrations/EventStoreDbContextModelSnapshot.cs", new string('\n', blanks) + "partial class EventStoreDbContextModelSnapshot { }");

    private static ProjectInventory Solution() => ProjectInventoryEngine.Build(
        [
            Project("src/Orders/Store/Store.csproj"), Snapshot("src/Orders/Store", 3),
            Project("src/Payments/Store/Store.csproj"), Snapshot("src/Payments/Store", 7),
            InventoryTestSupport.Code("src/Orders/Api.cs", "public static class A { public static void M(WebApplication app) { app.MapGet(\"/o\", (Snapshot s) => s); } }"),
        ],
        [Store("Snapshot"), Store("Snapshot", "Event")],
        null,
        ["src/Orders/Store/Migrations/EventStoreDbContextModelSnapshot.cs", "src/Payments/Store/Migrations/EventStoreDbContextModelSnapshot.cs"]);

    [Fact]
    public void TwoContextsOfOneName_GetDistinctIds_FromTheirSnapshotFolder()
    {
        var inventory = Solution();

        inventory.Of(ItemKind.Context).Select(c => c.Id).Should().Equal(
            "context:EventStoreDbContext [src/Orders/Store]", "context:EventStoreDbContext [src/Payments/Store]");
        inventory.Items.Select(i => i.Id).Should().OnlyHaveUniqueItems();
        inventory.Of(ItemKind.Entity).Select(e => e.Id).Should().Contain(
            ["entity:EventStoreDbContext [src/Orders/Store].Snapshot", "entity:EventStoreDbContext [src/Payments/Store].Event"]);
    }

    [Fact]
    public void EachContextPointsAtItsOwnSnapshot_NotTheFirstOneFound()
    {
        var inventory = Solution();

        inventory.Item("context:EventStoreDbContext [src/Orders/Store]").Source.Should().Be(new SourceLocation("src/Orders/Store/Migrations/EventStoreDbContextModelSnapshot.cs", 4));
        inventory.Item("context:EventStoreDbContext [src/Payments/Store]").Source.Should().Be(new SourceLocation("src/Payments/Store/Migrations/EventStoreDbContextModelSnapshot.cs", 8));
    }

    [Fact]
    public void AnEntityNameBothContextsShare_IsListedWithEachContext_InTheLinks()
    {
        var inventory = Solution();

        inventory.Items.Single(i => i.Kind == ItemKind.Endpoint).Attributes["touches"].Should().Be("EventStoreDbContext.Snapshot");
    }
}
