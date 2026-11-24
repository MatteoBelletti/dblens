using DbLens.Docs.Documents;
using DbLens.Docs.Inventory;
using FluentAssertions;
using static DbLens.Tests.Docs.InventoryTestSupport;

namespace DbLens.Tests.Docs;

/// <summary>M2's exit criterion: the check fails on an item that is missing, one that is gone, and a region touched by hand.</summary>
public sealed class DocumentCheckTests
{
    private static readonly DocsOptions Options = new();

    private static ProjectInventory Solution(params string[] routes) => Build(
        Project("src/Api/Api.csproj", "Microsoft.NET.Sdk.Web"),
        InventoryTestSupport.Code("src/Api/Api.cs", "public static class A { public static void M(WebApplication app) { " + string.Concat(routes.Select((r, i) => $"app.MapGet(\"{r}\", H{i});")) + " } }"));

    private static Dictionary<string, string> Written(ProjectInventory inventory) =>
        new(Scaffolder.Scaffold(inventory, new Dictionary<string, string>(), Options).Files);

    private static DocCheckResult Check(ProjectInventory inventory, Dictionary<string, string> documents, bool strict = false, bool requireSlots = false) =>
        DocumentCheck.Check(inventory, documents, Options, strict, requireSlots);

    private static string[] Codes(DocCheckResult result, IssueSeverity severity) =>
        [.. result.Issues.Where(i => i.Severity == severity).Select(i => i.Code).Distinct().Order(StringComparer.Ordinal)];

    [Fact]
    public void ADocumentJustScaffolded_PassesWithOnlyTheEmptySlotsAsWarnings()
    {
        var inventory = Solution("/a", "/b");

        var result = Check(inventory, Written(inventory));

        result.Ok.Should().BeTrue();
        Codes(result, IssueSeverity.Warning).Should().Equal("slot-empty");
        result.Warnings.Should().Be(3, "the project and two endpoints");
    }

    [Fact]
    public void AnItemWithNoRegion_Fails()
    {
        var documents = Written(Solution("/a"));

        var result = Check(Solution("/a", "/new"), documents);

        result.Ok.Should().BeFalse();
        result.Issues.Should().ContainSingle(i => i.Code == "missing-item" && i.Id == "endpoint:GET /new" && i.File == "docs/project/api.md");
    }

    [Fact]
    public void ARegionForAnItemThatNoLongerExists_Fails()
    {
        var documents = Written(Solution("/a", "/gone"));

        var result = Check(Solution("/a"), documents);

        result.Issues.Should().ContainSingle(i => i.Code == "orphan-item" && i.Id == "endpoint:GET /gone");
        result.Ok.Should().BeFalse();
    }

    [Fact]
    public void ARegionGeneratedByHandThatWasEdited_Fails()
    {
        var inventory = Solution("/a");
        var documents = Written(inventory);
        documents["docs/project/api.md"] = documents["docs/project/api.md"].Replace("| GET | /a |", "| GET | /a-but-not-really |");

        var result = Check(inventory, documents);

        result.Issues.Should().ContainSingle(i => i.Code == "generated-edited" && i.Id == "endpoint:GET /a");
    }

    [Fact]
    public void AnItemThatChangedSinceTheDocumentWasGenerated_IsOutdated()
    {
        var documents = Written(Solution("/a"));
        var changed = Solution("/a").WithHandlerRenamed();
        changed = changed with { Items = [.. changed.Items.Select(i => i.Id == "endpoint:GET /a" ? i with { Attributes = new SortedDictionary<string, string>(i.Attributes.ToDictionary(a => a.Key, a => a.Key == "handler" ? "Other" : a.Value), StringComparer.Ordinal) } : i)] };

        Check(changed, documents).Issues.Should().ContainSingle(i => i.Code == "outdated-item" && i.Id == "endpoint:GET /a");
    }

    [Fact]
    public void GeneratedTextThatTheItemHashDoesNotCover_StillMakesTheRegionOutdated()
    {
        var documents = Written(Solution("/a"));
        var moved = Solution("/a");
        moved = moved with { Items = [.. moved.Items.Select(i => i.Id == "endpoint:GET /a" ? i with { Source = new SourceLocation("src/Other/Moved.cs", 3) } : i)] };

        Check(moved, documents).Issues.Should().ContainSingle(i => i.Code == "outdated-item" && i.Id == "endpoint:GET /a");

        var withReference = Build(
            Project("src/Api/Api.csproj", "Microsoft.NET.Sdk.Web", "<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Core\\Core.csproj\" /></ItemGroup>"),
            Project("src/Core/Core.csproj"),
            InventoryTestSupport.Code("src/Api/Api.cs", "public static class A { public static void M(WebApplication app) { app.MapGet(\"/a\", H0); } }"));
        var withoutReference = Build(
            Project("src/Api/Api.csproj", "Microsoft.NET.Sdk.Web"),
            Project("src/Core/Core.csproj"),
            InventoryTestSupport.Code("src/Api/Api.cs", "public static class A { public static void M(WebApplication app) { app.MapGet(\"/a\", H0); } }"));

        Check(withReference, Written(withoutReference)).Issues.Should().Contain(i => i.Code == "outdated-item" && i.Id == ProjectGraph.Id);
    }

    [Fact]
    public void ARegionWhoseSlotWasDeleted_IsAWarning_AndAnErrorWhenSlotsAreRequired()
    {
        var inventory = Solution("/a");
        var documents = Written(inventory);
        var parsed = DocumentModel.Parse(documents["docs/project/api.md"]);
        documents["docs/project/api.md"] = DocumentModel.Render(parsed with { Blocks = [.. parsed.Blocks.Select(b => b with { Slot = null })] });

        Check(inventory, documents).Issues.Should().Contain(i => i.Code == "slot-missing" && i.Severity == IssueSeverity.Warning);
        Check(inventory, documents, requireSlots: true).Ok.Should().BeFalse();
        Check(inventory, documents, strict: true).Ok.Should().BeFalse();
    }

    [Fact]
    public void AnIdThatHoldsTheEscapes_SurvivesParseAndRender()
    {
        var inventory = Solution("/a%3Eb", "/c--d>e");
        var documents = Written(inventory);

        Check(inventory, documents).Issues.Should().NotContain(i => i.Severity == IssueSeverity.Error);
        DocumentModel.Parse(documents["docs/project/api.md"]).Blocks.Select(b => b.Id).Should().Contain(["endpoint:GET /a%3Eb", "endpoint:GET /c--d>e"]);
    }

    [Fact]
    public void ASlotOverItsLimit_Fails()
    {
        var inventory = Solution("/a");
        var documents = Written(inventory);
        documents["docs/project/api.md"] = Fill(documents["docs/project/api.md"], "endpoint:GET /a", "one\ntwo\nthree");

        Check(inventory, documents).Issues.Should().ContainSingle(i => i.Code == "slot-too-long" && i.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void EmptyAndStaleSlots_AreWarnings_UnlessTheRunAsksForMore()
    {
        var inventory = Solution("/a");
        var documents = Written(inventory);
        documents["docs/project/api.md"] = Fill(documents["docs/project/api.md"], "endpoint:GET /a", "Lists a.");
        var stale = Scaffolder.Scaffold(inventory.WithAttribute("endpoint:GET /a", "handler", "Other"), documents, Options);

        Check(inventory, documents).Warnings.Should().Be(1, "only the project slot is empty");
        Check(inventory, documents, requireSlots: true).Issues.Should().Contain(i => i.Code == "slot-empty" && i.Severity == IssueSeverity.Error);

        // The slot is stale against the new item, whatever the generated text says.
        var changed = inventory.WithAttribute("endpoint:GET /a", "handler", "Other");
        var afterScaffold = stale.Files.ToDictionary(f => f.Key, f => f.Value);
        Check(changed, afterScaffold).Ok.Should().BeTrue();
        Check(changed, afterScaffold).Issues.Should().Contain(i => i.Code == "slot-stale" && i.Severity == IssueSeverity.Warning);
        Check(changed, afterScaffold, strict: true).Ok.Should().BeFalse();
    }

    [Fact]
    public void WordsEditedByHandButStillMarkedInferred_AreAWarning()
    {
        var inventory = Solution("/a");
        var documents = Written(inventory);
        documents["docs/project/api.md"] = Fill(documents["docs/project/api.md"], "endpoint:GET /a", "Lists a.");
        documents["docs/project/api.md"] = documents["docs/project/api.md"].Replace("Lists a.", "Lists a, sorted.");

        Check(inventory, documents).Issues.Should().Contain(i => i.Code == "slot-unmarked-edit" && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void ADuplicatedRegion_AndAMalformedFile_Fail()
    {
        var inventory = Solution("/a");
        var documents = Written(inventory);
        documents["docs/project/configuration.md"] = documents["docs/project/api.md"];
        documents["docs/project/integrations.md"] = "<!-- dblens:item http:X hash=12345678 gen=12345678 -->\n";

        Codes(Check(inventory, documents), IssueSeverity.Error).Should().Contain(["duplicate-item", "malformed", "orphan-item"]);
    }

    [Fact]
    public void NoDocumentsAtAll_ReportsEveryItemMissing_NotASilentPass()
    {
        var result = Check(Solution("/a"), []);

        result.Issues.Where(i => i.Code == "missing-item").Select(i => i.Id).Should().BeEquivalentTo("project:Api", "endpoint:GET /a", ProjectGraph.Id);
    }

    internal static string Fill(string document, string id, string words)
    {
        var parsed = DocumentModel.Parse(document);
        return DocumentModel.Render(parsed with { Blocks = [.. parsed.Blocks.Select(b => b.Id == id ? b with { Slot = b.Slot! with { Text = words, TextHash = ItemHash.OfText(words) } } : b)] });
    }
}

internal static class InventoryTestExtensions
{
    public static ProjectInventory WithoutItem(this ProjectInventory inventory, string id) => inventory with { Items = [.. inventory.Items.Where(i => i.Id != id)] };

    public static ProjectInventory WithAttribute(this ProjectInventory inventory, string id, string key, string value) => inventory with
    {
        Items = [.. inventory.Items.Select(i => i.Id == id
            ? i with { Attributes = new SortedDictionary<string, string>(i.Attributes.ToDictionary(a => a.Key, a => a.Key == key ? value : a.Value), StringComparer.Ordinal) }
            : i)],
    };
}
