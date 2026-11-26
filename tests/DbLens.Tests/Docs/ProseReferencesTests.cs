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
    [InlineData("`Nothing: Create`", 1)]
    public void ACitationIsMissing_WhenNoFileOrTypeDeclaresThatMember(string text, int missing) =>
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

        var set = () => SlotWriter.Set(inventory, documents, Options, "detail:Ordering", "See `Apis/OrdersApi.cs: Gone` and `Nothing: At`.", Sources());

        set.Should().Throw<ArgumentException>().WithMessage("*not in the code*`Apis/OrdersApi.cs: Gone`*`Nothing: At`*");
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
    [InlineData("## How it works\n\nIt creates.\n\n## Not verified\n\n- the domain", false)]
    [InlineData("Come funziona.\n\n## Non verificato\n\n- il dominio", false)]
    public void APageAboutAComponent_SaysWhatWasNotRead(string words, bool warned)
    {
        var (inventory, documents) = Started(Sources());
        var (path, text) = SlotWriter.Set(inventory, documents, Options, "detail:Ordering", words);
        documents[path] = text;

        DocumentCheck.Check(inventory, documents, Options).Issues.Any(i => i.Code == "slot-no-unverified-section").Should().Be(warned);
    }

    [Theory]
    [InlineData("`OrderStatus: Submitted`")]
    [InlineData("`Orders.cs: Submitted` and `Orders.cs: Retries` and `Orders.cs: Changed`")]
    [InlineData("`OrderService: OrderService` and `OrderService: repository`")]
    [InlineData("`OrderStatus: Paid`")]
    public void WhatATypeHolds_AnEnumMemberAFieldAnEventAPrimaryConstructor_CanBeCited(string text) =>
        new ProseReferences([File("src/Ordering/Orders.cs",
                "public enum OrderStatus { Submitted, Paid } public class Orders { public const int Retries = 3; public event System.Action Changed; } public class OrderService(IRepository repository) { }")])
            .Check(text).Missing.Should().BeEmpty();

    [Fact]
    public void TheHashOfTheCitedCode_DoesNotDependOnTheLineEndingsOfTheCheckout()
    {
        const string text = "See `Notes.cs: Run`.";
        const string body = "public class Notes { public string Run() { /* one\n two */ return @\"a\nb\"; } }";

        new ProseReferences([File("Notes.cs", body)]).Check(text).Hash
            .Should().Be(new ProseReferences([File("Notes.cs", body.Replace("\n", "\r\n", StringComparison.Ordinal))]).Check(text).Hash);
    }

    [Fact]
    public void ThePrimaryConstructorIsWhatIsCited_NotTheWholeType()
    {
        const string text = "It takes its repository in `OrderService: OrderService`.";
        var before = new ProseReferences([File("S.cs", "public class OrderService(IRepository repository) { public void A() { } }")]).Check(text).Hash;

        new ProseReferences([File("S.cs", "public class OrderService(IRepository repository) { public void A() { var x = 1; } }")]).Check(text).Hash.Should().Be(before);
        new ProseReferences([File("S.cs", "public class OrderService(IRepository other) { public void A() { } }")]).Check(text).Hash.Should().NotBe(before);
    }

    [Fact]
    public void APersonWhoChangesTheCitations_IsNotToldTheCitedCodeChanged_ForWordsNoToolCanWriteAgain()
    {
        var (inventory, documents) = Started(Sources());
        var (path, text) = SlotWriter.Set(inventory, documents, Options, "detail:Ordering", Words, Sources());
        documents[path] = text.Replace("It creates in `Apis/OrdersApi.cs: Create`.", "It creates orders.", StringComparison.Ordinal);

        var issues = DocumentCheck.Check(inventory, documents, Options, strict: true, sources: Sources()).Issues;

        issues.Should().NotContain(i => i.Code == "slot-stale-reference");
        SlotWriter.List(inventory, documents, Options, Sources()).Single(s => s.Id == "detail:Ordering").State.Should().Be("current");
    }

    [Theory]
    [InlineData("## Not verified:\n\n- the domain")]
    [InlineData("## 7. Not verified (what I did not read)\n\n- the domain")]
    [InlineData("### Non verificato\n\n- il dominio")]
    public void TheNotVerifiedSection_IsRecognisedWithANumberOrAnExplanationInItsHeading(string heading)
    {
        var (inventory, documents) = Started(Sources());
        var (path, text) = SlotWriter.Set(inventory, documents, Options, "detail:Ordering", "How it works.\n\n" + heading);
        documents[path] = text;

        DocumentCheck.Check(inventory, documents, Options).Issues.Should().NotContain(i => i.Code == "slot-no-unverified-section");
    }

    [Fact]
    public void WhenAPersonRewritesTheWords_TheHashOfTheOldCitationsGoesWithThem()
    {
        var (inventory, documents) = Started(Sources());
        var (path, text) = SlotWriter.Set(inventory, documents, Options, "detail:Ordering", Words, Sources());
        documents[path] = text.Replace("It creates in `Apis/OrdersApi.cs: Create`.", "A person's own paragraph.");

        var scaffolded = Scaffolder.Scaffold(inventory, documents, Options).Files[path];

        var slot = DocumentModel.Parse(scaffolded).Blocks.Single(b => b.Slot is not null).Slot!;
        (slot.Evidence, slot.RefsHash).Should().Be(("human", null));
        DocumentCheck.Check(inventory, new Dictionary<string, string>(documents) { [path] = scaffolded }, Options, strict: true, sources: Sources()).Issues
            .Should().NotContain(i => i.Code.EndsWith("reference", StringComparison.Ordinal));
    }
}
