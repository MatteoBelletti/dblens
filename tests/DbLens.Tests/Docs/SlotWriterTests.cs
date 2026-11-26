using DbLens.Docs.Documents;
using DbLens.Docs.Inventory;
using FluentAssertions;
using static DbLens.Tests.Docs.InventoryTestSupport;

namespace DbLens.Tests.Docs;

public sealed class SlotWriterTests
{
    private static readonly DocsOptions Options = new();
    private const string Endpoint = "endpoint:GET /a";

    private static ProjectInventory Solution() => Build(
        Project("src/Api/Api.csproj", "Microsoft.NET.Sdk.Web"),
        InventoryTestSupport.Code("src/Api/Api.cs", "public static class A { public static void M(WebApplication app) { app.MapGet(\"/a\", H); } }"));

    private static Dictionary<string, string> Written(ProjectInventory inventory) =>
        new(Scaffolder.Scaffold(inventory, new Dictionary<string, string>(), Options).Files);

    private static Dictionary<string, string> WithWords(ProjectInventory inventory, string words)
    {
        var documents = Written(inventory);
        var (path, text) = SlotWriter.Set(inventory, documents, Options, Endpoint, words);
        documents[path] = text;
        return documents;
    }

    [Fact]
    public void List_ShowsTheEmptySlotsWithTheFactsOfTheirItem()
    {
        var inventory = Solution();

        var slots = SlotWriter.List(inventory, Written(inventory), Options);

        slots.Should().OnlyContain(s => s.State == "empty" && !s.Protected);
        var endpoint = slots.Single(s => s.Id == Endpoint);
        endpoint.File.Should().Be("docs/project/api.md");
        endpoint.MaxLines.Should().Be(2);
        endpoint.Facts.Should().ContainKey("handler");
        endpoint.Source.Should().Be("src/Api/Api.cs:1");
    }

    [Fact]
    public void Set_WritesTheWordsAsInferred_AndTheCheckAcceptsThem()
    {
        var inventory = Solution();

        var documents = WithWords(inventory, "Lists a.\nSorted by name.");

        SlotWriter.List(inventory, documents, Options).Single(s => s.Id == Endpoint).Should().Match<SlotInfo>(s => s.State == "current" && s.Evidence == "inferred" && s.Text == "Lists a.\nSorted by name.");
        DocumentCheck.Check(inventory, documents, Options).Issues.Should().NotContain(i => i.Id == Endpoint);
    }

    [Fact]
    public void WordsSurviveAScaffold_AndTurnStaleWhenTheItemChanges()
    {
        var inventory = Solution();
        var documents = WithWords(inventory, "Lists a.");

        Scaffolder.Scaffold(inventory, documents, Options).Report.Stale.Should().BeEmpty();

        var changed = inventory.WithAttribute(Endpoint, "handler", "Other");
        var after = Scaffolder.Scaffold(changed, documents, Options).Files.ToDictionary(f => f.Key, f => f.Value);
        SlotWriter.List(changed, after, Options).Single(s => s.Id == Endpoint).State.Should().Be("stale");
    }

    [Fact]
    public void APersonsWords_AreNeverReplaced_EvenWhenTheyWentStale()
    {
        var inventory = Solution();
        var documents = WithWords(inventory, "Lists a.");
        documents["docs/project/api.md"] = Scaffolder.Confirm(documents["docs/project/api.md"], Endpoint, ItemHash.Of(inventory.Item(Endpoint)));

        SlotWriter.List(inventory, documents, Options).Single(s => s.Id == Endpoint).Protected.Should().BeTrue();
        var set = () => SlotWriter.Set(inventory, documents, Options, Endpoint, "Mine now.");
        set.Should().Throw<ArgumentException>().WithMessage("*person*");

        var changed = inventory.WithAttribute(Endpoint, "handler", "Other");
        var stale = Scaffolder.Scaffold(changed, documents, Options).Files.ToDictionary(f => f.Key, f => f.Value);
        SlotWriter.List(changed, stale, Options).Single(s => s.Id == Endpoint).Should().Match<SlotInfo>(s => s.State == "stale" && s.Protected);
        var again = () => SlotWriter.Set(changed, stale, Options, Endpoint, "Mine now.");
        again.Should().Throw<ArgumentException>().WithMessage("*person*");
    }

    [Fact]
    public void WordsAPersonEditedBeforeAnyScaffold_AreProtected_AndStayProtectedThroughOneThatFollows()
    {
        var inventory = Solution();
        var documents = WithWords(inventory, "Lists a.");
        documents["docs/project/api.md"] = documents["docs/project/api.md"].Replace("Lists a.", "Lists a, as I know it.");

        SlotWriter.List(inventory, documents, Options).Single(s => s.Id == Endpoint).Protected.Should().BeTrue("the marker still says inferred");
        var set = () => SlotWriter.Set(inventory, documents, Options, Endpoint, "Overwritten.");
        set.Should().Throw<ArgumentException>().WithMessage("*person*");

        // Same edit on words that had already gone stale: the person's, not the model's.
        var changed = inventory.WithAttribute(Endpoint, "handler", "Other");
        var staleThenEdited = Scaffolder.Scaffold(changed, WithWords(inventory, "Lists a."), Options).Files.ToDictionary(f => f.Key, f => f.Value);
        staleThenEdited["docs/project/api.md"] = staleThenEdited["docs/project/api.md"].Replace("Lists a.", "Lists a, as I know it.");
        SlotWriter.List(changed, staleThenEdited, Options).Single(s => s.Id == Endpoint).Protected.Should().BeTrue();

        var scaffolded = Scaffolder.Scaffold(changed, staleThenEdited, Options).Files.ToDictionary(f => f.Key, f => f.Value);
        SlotWriter.List(changed, scaffolded, Options).Single(s => s.Id == Endpoint).Should().Match<SlotInfo>(s => s.State == "stale" && s.Protected && s.Text == "Lists a, as I know it.");
    }

    [Fact]
    public void AModelsStaleWords_MayBeRewritten()
    {
        var inventory = Solution();
        var documents = WithWords(inventory, "Lists a.");
        var changed = inventory.WithAttribute(Endpoint, "handler", "Other");
        var stale = Scaffolder.Scaffold(changed, documents, Options).Files.ToDictionary(f => f.Key, f => f.Value);

        var (path, text) = SlotWriter.Set(changed, stale, Options, Endpoint, "Lists a again.");

        stale[path] = text;
        SlotWriter.List(changed, stale, Options).Single(s => s.Id == Endpoint).Should().Match<SlotInfo>(s => s.State == "current" && s.Text == "Lists a again.");
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("one\ntwo\nthree", "at most 2")]
    [InlineData("a\n\nb", "Blank lines")]
    [InlineData("see <!-- dblens:end -->", "HTML comment")]
    [InlineData("-->", "HTML comment")]
    public void BadWords_AreRefused_AndTheDocumentIsNotChanged(string words, string reason)
    {
        var inventory = Solution();
        var documents = Written(inventory);

        var set = () => SlotWriter.Set(inventory, documents, Options, Endpoint, words);

        set.Should().Throw<ArgumentException>().WithMessage($"*{reason}*");
    }

    [Fact]
    public void ALineThatIsTooLong_IsRefused()
    {
        var inventory = Solution();
        var set = () => SlotWriter.Set(inventory, Written(inventory), Options, Endpoint, new string('x', SlotWriter.MaxLineLength + 1));

        set.Should().Throw<ArgumentException>().WithMessage("*longer than*");
    }

    [Theory]
    [InlineData("endpoint:GET /nowhere", "not in the inventory")]
    [InlineData("config:Nothing", "not in the inventory")]
    public void AnUnknownItem_IsRefused(string id, string reason)
    {
        var inventory = Solution();
        var set = () => SlotWriter.Set(inventory, Written(inventory), Options, id, "words");

        set.Should().Throw<ArgumentException>().WithMessage($"*{reason}*");
    }

    [Fact]
    public void AnItemThatHasNoSlot_AndADocumentThatIsOutdated_AreRefused()
    {
        var inventory = Solution().WithExtraItem(new InventoryItem(
            "config:Mail", ItemKind.Config, "Mail", ItemEvidence.Extracted, new SourceLocation("x.json", 1), new SortedDictionary<string, string>(StringComparer.Ordinal) { ["readIn"] = "code" }, [], null));
        var documents = Written(inventory);

        var noSlot = () => SlotWriter.Set(inventory, documents, Options, "config:Mail", "words");
        noSlot.Should().Throw<ArgumentException>().WithMessage("*no slot*");

        var changed = inventory.WithAttribute(Endpoint, "handler", "Other");
        var outdated = () => SlotWriter.Set(changed, documents, Options, Endpoint, "words");
        outdated.Should().Throw<ArgumentException>().WithMessage("*run dblens-docs scaffold*");
    }
}

internal static class InventoryExtraTestExtensions
{
    public static ProjectInventory WithExtraItem(this ProjectInventory inventory, InventoryItem item) => inventory with { Items = [.. inventory.Items, item] };
}
