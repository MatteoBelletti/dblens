using DbLens.Docs.Cli;
using DbLens.Docs.Documents;
using DbLens.Docs.Inventory;
using FluentAssertions;

namespace DbLens.Tests.Docs;

/// <summary>The detail of one component: a document that exists only when someone asks for it, with a page of narrative.</summary>
public sealed class DetailDocumentTests : IDisposable
{
    private static readonly DocsOptions Options = new();
    private const string DetailPath = "docs/project/components/ordering.md";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dblens-docs-detail-");

    public void Dispose() => _root.Delete(recursive: true);

    private static ProjectInventory Solution() => OverviewTests.OrderFlow();

    private static Dictionary<string, string> Started(ProjectInventory inventory, params string[] components) =>
        new(Scaffolder.Scaffold(inventory, new Dictionary<string, string>(), Options, components).Files);

    [Fact]
    public void NoDetailDocument_ExistsUntilSomeoneAsksForOne()
    {
        var files = Scaffolder.Scaffold(Solution(), new Dictionary<string, string>(), Options).Files;

        files.Keys.Should().NotContain(k => k.Contains("/components/", StringComparison.Ordinal));
    }

    [Fact]
    public void AskingForAComponent_StartsItsDocument_WithTheFactsOfTheWholeInventoryAndOneNarrativeSlot()
    {
        var files = Started(Solution(), "Ordering");

        var document = files[DetailPath];
        document.Should().StartWith("# Ordering").And.Contain("dblens:generated detail-facts:Ordering")
            .And.Contain("### Endpoints").And.Contain("/orders")
            .And.Contain("### Events it publishes").And.Contain("OrderPlacedIntegrationEvent")
            .And.Contain("### Flows it takes part in")
            .And.Contain("dblens:slot detail:Ordering purpose max-lines=80");
        Started(Solution(), "Billing")[("docs/project/components/billing.md")].Should().Contain("### Events it consumes").And.Contain("OrderPlacedHandler");
    }

    [Fact]
    public void ADetailDocumentThatExists_IsKeptUpToDate_AndItsWordsStayIntheirSlot()
    {
        var inventory = Solution();
        var files = Started(inventory, "Ordering");
        var (path, text) = SlotWriter.Set(inventory, files, Options, "detail:Ordering", "## What it does\n\nIt places orders.\n\n## Not verified\n\n- none");
        files[path] = text;

        var again = Scaffolder.Scaffold(inventory, files, Options);

        again.Files[DetailPath].Should().Contain("It places orders.");
        again.Report.Added.Should().BeEmpty();
        DocumentCheck.Check(inventory, again.Files.ToDictionary(f => f.Key, f => f.Value), Options).Issues.Where(i => i.Severity == IssueSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void TheDetailDocumentOfAComponentThatIsGone_IsAnOrphan_UntilScaffoldEmptiesIt()
    {
        var inventory = Solution();
        var files = Started(inventory, "Ordering");
        var gone = ProjectInventoryEngine.Build([], []);

        DocumentCheck.Check(gone, files, Options).Issues.Should().Contain(i => i.Code == "orphan-item" && i.Id == "detail:Ordering", "until scaffold runs, the document is an orphan, not a silent pass");
        var again = Scaffolder.Scaffold(gone, files, Options);

        again.Report.Removed.Should().Contain(r => r.StartsWith("detail:Ordering", StringComparison.Ordinal));
        again.Files[DetailPath].Should().NotContain("dblens:slot");
        DocumentCheck.Check(gone, again.Files.ToDictionary(f => f.Key, f => f.Value), Options).Issues.Where(i => i.Severity == IssueSeverity.Error).Should().BeEmpty();
    }

    [Fact]
    public void ConfirmingTheNarrative_MakesItCurrent()
    {
        var inventory = Solution();
        var files = Started(inventory, "Ordering");
        var (path, text) = SlotWriter.Set(inventory, files, Options, "detail:Ordering", "It places orders.\n\n## Not verified\n\n- none");
        files[path] = text;

        files[path] = Scaffolder.Confirm(files[path], "detail:Ordering", SlotWriter.CurrentHash(inventory, files, Options, "detail:Ordering"));

        SlotWriter.List(inventory, files, Options).Single(s => s.Id == "detail:Ordering").State.Should().Be("current");
        DocumentCheck.Check(inventory, files, Options, strict: true).Issues.Should().NotContain(i => i.Code == "slot-stale");
    }

    [Fact]
    public void TwoComponentsWithTheSameDocumentName_AreRefused()
    {
        var inventory = InventoryTestSupport.Build(
            InventoryTestSupport.Web("src/Ordering.API/Ordering.API.csproj"), InventoryTestSupport.Web("src/Ordering-API/Ordering-API.csproj"));

        new Action(() => Scaffolder.Scaffold(inventory, new Dictionary<string, string>(), Options, ["Ordering.API"]))
            .Should().Throw<ArgumentException>().WithMessage("*would share the document*");
    }

    [Fact]
    public void ANarrativeSlot_MayHaveParagraphsAndHeadings_ACaptionMayNot()
    {
        var inventory = Solution();
        var files = Started(inventory, "Ordering");

        new Action(() => SlotWriter.Set(inventory, files, Options, "detail:Ordering", "Paragraph one.\n\nParagraph two.\n\n## Not verified\n\n- none")).Should().NotThrow();
        new Action(() => SlotWriter.Set(inventory, files, Options, "flow:endpoint:POST /orders", "One.\n\nTwo.")).Should().Throw<ArgumentException>().WithMessage("*Blank lines*");
        new Action(() => SlotWriter.Set(inventory, files, Options, "detail:Ordering", string.Join('\n', Enumerable.Repeat("line", 81)))).Should().Throw<ArgumentException>().WithMessage("*at most 80*");
    }

    [Fact]
    public void SlotsOfADetailDocument_AreListedLikeAnyOther_WithTheirState()
    {
        var inventory = Solution();
        var files = Started(inventory, "Ordering");

        SlotWriter.List(inventory, files, Options).Should().Contain(s => s.Id == "detail:Ordering" && s.State == "empty" && s.MaxLines == 80 && s.File == DetailPath);
    }

    [Fact]
    public void TheCommand_StartsTheDocument_RefusesAnUnknownComponent_AndWritesOnlyInsideTheFolder()
    {
        void Write(string relative, string text)
        {
            var path = Path.Combine(_root.FullName, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        Write("src/Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write("src/Api/A.cs", "public static class A { public static void M(WebApplication app) { app.MapGet(\"/a\", H); } }");
        StringWriter output = new(), error = new();

        DocsCli.Run(["detail", "api"], output, error, _root.FullName).Should().Be(0, error.ToString());
        File.Exists(Path.Combine(_root.FullName, "docs", "project", "components", "api.md")).Should().BeTrue();
        output.ToString().Should().Contain("components/api.md").And.Contain("detail:Api");

        StringWriter error2 = new();
        DocsCli.Run(["detail", "Nope"], new StringWriter(), error2, _root.FullName).Should().Be(2);
        error2.ToString().Should().Contain("Components: Api");
    }

    [Fact]
    public void ADetailFolderThatIsALink_IsRefused_AndOnlyTheKnownNamesAreWritten()
    {
        var elsewhere = Directory.CreateTempSubdirectory("dblens-docs-detail-elsewhere-");
        try
        {
            Directory.CreateDirectory(Path.Combine(_root.FullName, "docs", "project"));
            Directory.CreateSymbolicLink(Path.Combine(_root.FullName, "docs", "project", "components"), elsewhere.FullName);

            new Action(() => DocsFolder.Write(_root.FullName, Options, new Dictionary<string, string> { [DetailPath] = "x" })).Should().Throw<ArgumentException>().WithMessage("*link*");
            new Action(() => DocsFolder.Write(_root.FullName, Options, new Dictionary<string, string> { ["docs/project/components/..%2Fx.md"] = "x" })).Should().Throw<ArgumentException>();
            elsewhere.GetFiles().Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(Path.Combine(_root.FullName, "docs", "project", "components"));
            elsewhere.Delete(recursive: true);
        }
    }
}
