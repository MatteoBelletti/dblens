using DbLens.Docs.Inventory;
using FluentAssertions;

namespace DbLens.Tests.Docs;

/// <summary>M1's exit criterion: the same files give the same JSON on every run.</summary>
public sealed class InventoryDeterminismTests
{
    [Fact]
    public void TheSameFolder_GivesTheSameBytes_OnEveryRun()
    {
        var first = InventoryJson.Serialize(ProjectInventoryEngine.Build(DocsFixtures.EShop()));
        var second = InventoryJson.Serialize(ProjectInventoryEngine.Build(DocsFixtures.EShop()));

        second.Should().Be(first);
    }

    [Fact]
    public void TheOrderTheFilesArriveIn_DoesNotMatter()
    {
        var read = DocsFixtures.EShop();
        var shuffled = new SolutionRead([.. read.Files.OrderBy(f => f.Path.GetHashCode(StringComparison.Ordinal) * 31 + f.Path.Length)], [.. read.Models.AsEnumerable().Reverse()], read.Coverage) { ModelSources = [.. read.ModelSources.AsEnumerable().Reverse()] };

        InventoryJson.Serialize(ProjectInventoryEngine.Build(shuffled)).Should().Be(InventoryJson.Serialize(ProjectInventoryEngine.Build(read)));
    }

    [Fact]
    public void TheJson_HasNoClockAndNoAbsolutePath()
    {
        var json = InventoryJson.Serialize(ProjectInventoryEngine.Build(DocsFixtures.EShop()));

        json.Should().NotContain(AppContext.BaseDirectory).And.NotContain("generatedAt").And.NotContain("\\\\");
        json.Should().StartWith("{\n  \"schemaVersion\": 1,").And.EndWith("}\n");
    }

    [Theory]
    [InlineData("eShop", false)]
    [InlineData("DemoShop", true)]
    public void TheInventory_MatchesItsReferenceFile(string name, bool demo)
    {
        var current = InventoryJson.Serialize(ProjectInventoryEngine.Build(demo ? DocsFixtures.DemoShop() : DocsFixtures.EShop()));
        var reference = Path.Combine(DocsFixtures.ReferenceFolder, $"{name}.inventory.json");

        if (Environment.GetEnvironmentVariable("DBLENS_UPDATE_DOCS_REFERENCE") == "1") File.WriteAllText(reference, current);

        File.ReadAllText(reference).Should().Be(current,
            "a change of the inventory is recorded by running the tests once with DBLENS_UPDATE_DOCS_REFERENCE=1 and committing tests/DbLens.Tests/Docs/Reference");
    }
}
