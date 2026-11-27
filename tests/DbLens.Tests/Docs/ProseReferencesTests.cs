using DbLens.Docs.Documents;
using DbLens.Docs.Inventory;
using FluentAssertions;
using static DbLens.Tests.Docs.InventoryTestSupport;

namespace DbLens.Tests.Docs;

/// <summary>The words of a slot cite code, and the code answers: it is there, and it is what it was.</summary>
public sealed class ProseReferencesTests
{
    private static readonly DocsOptions Options = new();

    private static SourceFile Api(string body = "return 1;") =>
        File("src/Ordering/Apis/OrdersApi.cs", $"public class OrdersApi {{ public int Create() {{ {body} }} public int Create(int x) => x; public string Name {{ get; }} }}");

    private static SourceFile Other() => File("src/Ordering/Domain/Order.cs", "public class Order { public Order() { } public void Cancel() { } }");

    private static IReadOnlyList<SourceFile> Sources(params SourceFile[] extra) => [Web("src/Ordering/Ordering.csproj"), Api(), Other(), .. extra];

    [Fact]
    public void ACitationIsAFileOrATypeAndAMember_InBackticks_AndNothingElseIsOne() =>
        ProseReferences.Extract("See `Apis/OrdersApi.cs: Create`, `OrdersApi: Name`, `Guid.Empty`, Order: Cancel and `lower: case` and `Domain\\Order.cs: Cancel`.")
            .Select(r => r.ToString()).Should().Equal("Apis/OrdersApi.cs: Create", "OrdersApi: Name", "Domain/Order.cs: Cancel");

    [Theory]
    [InlineData("`Apis/OrdersApi.cs: Create`", 0)]
    [InlineData("`src/Ordering/Apis/OrdersApi.cs: Create` and `OrdersApi: Name` and `Order: Order` and `Order.Cancel`", 0)]
    [InlineData("`Apis/OrdersApi.cs: Gone`", 1)]
    [InlineData("`Apis/Missing.cs: Create`", 1)]
    [InlineData("`Order: Create`", 1)]
    [InlineData("`Nothing: Create`", 0)]
    [InlineData("`Order: Gone`", 1)]
    [InlineData("`Order: Order`", 0)]
    public void ACitationIsMissing_WhenTheFileOrTypeIsThere_AndDoesNotDeclareThatMember(string text, int missing) =>
        new ProseReferences(Sources()).Check(text).Missing.Should().HaveCount(missing);

    [Fact]
    public void TheHashOfWhatIsCited_ChangesWithTheCitedCode_AndWithNothingElse()
    {
        const string text = "It works in `Apis/OrdersApi.cs: Create`.";
        var before = new ProseReferences(Sources()).Check(text).Hash;

        new ProseReferences(Sources()).Check(text).Hash.Should().Be(before);
        new ProseReferences([Web("src/Ordering/Ordering.csproj"), Api("  return   1;  "), Other()]).Check(text).Hash.Should().Be(before, "spacing is not a change");
        new ProseReferences([Web("src/Ordering/Ordering.csproj"), Api(), File("src/Ordering/Domain/Order.cs", "public class Order { public void Cancel() { var x = 1; } }")]).Check(text).Hash.Should().Be(before, "another file is not what is cited");
        new ProseReferences([Web("src/Ordering/Ordering.csproj"), Api("return 2;"), Other()]).Check(text).Hash.Should().NotBe(before);
        new ProseReferences(Sources()).Check("No citation here.").Hash.Should().BeNull();
    }

    private static (ProjectInventory Inventory, Dictionary<string, string> Documents) Started(IReadOnlyList<SourceFile> sources)
    {
        var inventory = ProjectInventoryEngine.Build(sources, []);
        return (inventory, new(Scaffolder.Scaffold(inventory, new Dictionary<string, string>(), Options, ["Ordering"]).Files));
    }

    private const string Words = "## How it works\n\nIt creates in `Apis/OrdersApi.cs: Create`.\n\n## Not verified\n\n- the domain";

    [Fact]
    public void WordsThatCiteWhatIsNotThere_AreRefused_WithTheCitationsNamed()
    {
        var (inventory, documents) = Started(Sources());

        var set = () => SlotWriter.Set(inventory, documents, Options, "detail:Ordering", "See `Apis/OrdersApi.cs: Gone` and `Order: Missing`.\n\n## Not verified\n\n- all", Sources());

        set.Should().Throw<ArgumentException>().WithMessage("*not in the code*`Apis/OrdersApi.cs: Gone`*`Order: Missing`*");
    }

    [Fact]
    public void TheHashOfTheCitedCode_IsKeptInTheMarker_AndSurvivesAParse()
    {
        var (inventory, documents) = Started(Sources());

        var (path, text) = SlotWriter.Set(inventory, documents, Options, "detail:Ordering", Words, Sources());

        var slot = DocumentModel.Parse(text).Blocks.Single(b => b.Slot is not null).Slot!;
        slot.RefsHash.Should().NotBeNull();
        text.Should().Contain($" refs={slot.RefsHash} -->");
        DocumentModel.Parse(DocumentModel.Render(DocumentModel.Parse(text))).Blocks.Single(b => b.Slot is not null).Slot!.RefsHash.Should().Be(slot.RefsHash);
        path.Should().EndWith("ordering.md");
    }

    [Fact]
    public void WhenTheCitedCodeChanges_TheCheckSaysSo_AndTheSlotIsListedAsStale()
    {
        var (inventory, documents) = Started(Sources());
        var (path, text) = SlotWriter.Set(inventory, documents, Options, "detail:Ordering", Words, Sources());
        documents[path] = text;
        var changed = Sources().Select(s => s.Path.EndsWith("OrdersApi.cs", StringComparison.Ordinal) ? Api("return 2;") : s).ToList();

        DocumentCheck.Check(inventory, documents, Options, sources: Sources()).Issues.Should().NotContain(i => i.Code.EndsWith("reference", StringComparison.Ordinal));
        SlotWriter.List(inventory, documents, Options, Sources()).Single(s => s.Id == "detail:Ordering").State.Should().Be("current");

        var issues = DocumentCheck.Check(inventory, documents, Options, sources: changed).Issues;
        issues.Should().Contain(i => i.Code == "slot-stale-reference" && i.Severity == IssueSeverity.Warning);
        DocumentCheck.Check(inventory, documents, Options, strict: true, sources: changed).Issues.Should().Contain(i => i.Code == "slot-stale-reference" && i.Severity == IssueSeverity.Error);
        SlotWriter.List(inventory, documents, Options, changed).Single(s => s.Id == "detail:Ordering").State.Should().Be("stale");
    }

    [Fact]
    public void ACitationThatNoLongerHoldsInTheCode_IsABadReference()
    {
        var (inventory, documents) = Started(Sources());
        var (path, text) = SlotWriter.Set(inventory, documents, Options, "detail:Ordering", Words, Sources());
        documents[path] = text;
        var withoutMember = Sources().Select(s => s.Path.EndsWith("OrdersApi.cs", StringComparison.Ordinal) ? File(s.Path, "public class OrdersApi { }") : s).ToList();

        DocumentCheck.Check(inventory, documents, Options, sources: withoutMember).Issues.Should().Contain(i => i.Code == "slot-bad-reference" && i.Message.Contains("Apis/OrdersApi.cs: Create"));
    }

    [Theory]
    [InlineData("## How it works\n\nIt creates.", true)]
    [InlineData("## How it works\n\nIt creates.\n\n```\n## Not verified\n```", true)]
    [InlineData("## How it works\n\nIt creates.\n\n## Not verified\n\n- the domain", false)]
    [InlineData("## 7. Not verified (what I did not read)", false)]
    [InlineData("Come funziona.\n\n## Non verificato\n\n- il dominio", false)]
    public void APageAboutAComponent_SaysWhatWasNotRead(string words, bool warned)
    {
        var (inventory, documents) = Started(Sources());
        var path = documents.Keys.Single(k => k.Contains("/components/", StringComparison.Ordinal));
        var parsed = DocumentModel.Parse(documents[path]);
        documents[path] = DocumentModel.Render(parsed with { Blocks = [.. parsed.Blocks.Select(b => b.Slot is { } slot ? b with { Slot = slot with { Text = words, TextHash = ItemHash.OfText(words) } } : b)] });

        DocumentCheck.Check(inventory, documents, Options).Issues.Any(i => i.Code == "slot-no-unverified-section").Should().Be(warned);
    }

    [Fact]
    public void WordsWithoutTheSection_AreRefusedWhenAModelWritesThem_AndNotWarnedWhenAPersonDid()
    {
        var (inventory, documents) = Started(Sources());

        new Action(() => SlotWriter.Set(inventory, documents, Options, "detail:Ordering", "## How it works\n\nIt creates.")).Should().Throw<ArgumentException>().WithMessage("*\"Not verified\" section*");
        var italian = new DocsOptions("it");
        var inItalian = new Dictionary<string, string>(Scaffolder.Scaffold(inventory, new Dictionary<string, string>(), italian, ["Ordering"]).Files);
        new Action(() => SlotWriter.Set(inventory, inItalian, italian, "detail:Ordering", "Come funziona.")).Should().Throw<ArgumentException>().WithMessage("*\"Non verificato\" section*");
    }

    [Fact]
    public void AMemberInheritedFromABaseTypeInTheSources_IsCitedAndHashed_OneFromAFrameworkBaseIsNotChecked()
    {
        var sources = new ProseReferences([
            File("src/A/Base.cs", "public class Base { public void Run() { } }"),
            File("src/A/Child.cs", "public class Child : Base { }"),
            File("src/A/Store.cs", "public class Store : DbContext { }"),
            File("src/A/Plain.cs", "public class Plain { }")]);

        sources.Check("`Child: Run`").Should().Match<(IReadOnlyList<CodeReference> Missing, string? Hash)>(r => r.Missing.Count == 0 && r.Hash != null);
        sources.Check("`Store: SaveChangesAsync`").Should().Match<(IReadOnlyList<CodeReference> Missing, string? Hash)>(r => r.Missing.Count == 0 && r.Hash == null, "it may be inherited from the framework: nothing can answer it");
        sources.Check("`Plain: SaveChangesAsync`").Missing.Should().HaveCount(1);
    }

    [Fact]
    public void FieldsEnumMembersAndPrimaryConstructors_AreCitable_AndTheirHashSeesTheirTypeNotTheirComments()
    {
        string Hash(string code) => new ProseReferences([File("src/A/Orders.cs", code)]).Check("`Orders: Retries` `OrderStatus: Paid` `Orders: Orders`").Hash!;

        var before = Hash("public class Orders(int Orders2) { public const int Retries = 3; } public enum OrderStatus { New, Paid }");

        new ProseReferences([File("src/A/Orders.cs", "public class Orders(int Orders2) { public const int Retries = 3; } public enum OrderStatus { New, Paid }")])
            .Check("`Orders: Retries` `OrderStatus: Paid` `Orders: Orders`").Missing.Should().BeEmpty();
        Hash("public class Orders(int Orders2) {\n // retries\n public const   int Retries = 3; } public enum OrderStatus { New, Paid }").Should().Be(before, "a comment is not code");
        Hash("public class Orders(int Orders2) { public const long Retries = 3; } public enum OrderStatus { New, Paid }").Should().NotBe(before, "the type of a field is what it is");
    }
}
