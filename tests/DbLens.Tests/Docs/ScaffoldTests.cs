using DbLens.Docs.Documents;
using DbLens.Docs.Inventory;
using FluentAssertions;
using static DbLens.Tests.Docs.InventoryTestSupport;

namespace DbLens.Tests.Docs;

public sealed class ScaffoldTests
{
    private static readonly DocsOptions Options = new();

    private static ProjectInventory Solution(string route = "/items", string extra = "") => Build(
        Project("src/Api/Api.csproj", "Microsoft.NET.Sdk.Web"),
        Project("src/Core/Core.csproj", body: "<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Api\\Api.csproj\" /></ItemGroup>"),
        InventoryTestSupport.Code("src/Api/Api.cs", $$"""
            public static class Api
            {
                public static void Map(WebApplication app)
                {
                    app.MapGet("{{route}}", List);
                    {{extra}}
                }
            }
            """),
        File("src/Api/appsettings.json", "{ \"Mail\": { \"From\": \"x\" } }"));

    private static string Api(ScaffoldResult result) => result.Files["docs/project/api.md"];

    [Fact]
    public void FirstRun_WritesEveryItsPlace_AndTheGraph_AndNoDocumentForNothing()
    {
        var result = Scaffolder.Scaffold(Solution(), new Dictionary<string, string>(), Options);

        result.Files.Keys.Should().BeEquivalentTo("docs/project/architecture.md", "docs/project/api.md", "docs/project/configuration.md");
        result.Report.Added.Should().Contain(["project:Api", "project:Core", "endpoint:GET /items", "config:Mail:From", ProjectGraph.Id]);
        result.Files["docs/project/architecture.md"].Should().Contain("```mermaid\ngraph LR").And.Contain("p1 --> p0");
        Api(result).Should().StartWith("# API\n\nEndpoints declared in the code.").And.Contain("<!-- dblens:slot endpoint:GET /items purpose max-lines=2 evidence=inferred");
        result.Files["docs/project/configuration.md"].Should().NotContain("dblens:slot", "a configuration key has its place but no words");
    }

    [Fact]
    public void ARerunOnTheSameCode_ChangesNothing()
    {
        var first = Scaffolder.Scaffold(Solution(), new Dictionary<string, string>(), Options);
        var second = Scaffolder.Scaffold(Solution(), first.Files, Options);

        second.Files.Should().BeEquivalentTo(first.Files);
        second.Report.Should().Match<ScaffoldReport>(r => r.Added.Count == 0 && r.Removed.Count == 0 && r.Changed.Count == 0 && r.Stale.Count == 0);
    }

    [Fact]
    public void WhatAPersonWroteOutsideTheRegions_IsKept_AndWhatLeftTheCodeIsRemoved()
    {
        var first = Scaffolder.Scaffold(Solution(extra: "app.MapDelete(\"/gone\", Gone);"), new Dictionary<string, string>(), Options);
        var text = Api(first)
            .Replace("Endpoints declared in the code.", "Endpoints declared in the code. We call them from the mobile app.")
            .Replace("<!-- dblens:end -->\n\n<!-- dblens:item endpoint:GET /items", "<!-- dblens:end -->\n\nA note about the list endpoint.\n\n<!-- dblens:item endpoint:GET /items");
        var edited = new Dictionary<string, string>(first.Files) { ["docs/project/api.md"] = text };

        var second = Scaffolder.Scaffold(Solution(), edited, Options);

        Api(second).Should().Contain("We call them from the mobile app.").And.NotContain("/gone");
        second.Report.Removed.Should().ContainSingle("endpoint:DELETE /gone");
    }

    [Fact]
    public void WhenAnItemChanges_ItsGeneratedTextFollows_AndItsSlotsWordsStayButBecomeStale()
    {
        var first = Scaffolder.Scaffold(Solution(), new Dictionary<string, string>(), Options);
        var withWords = Fill(first.Files, "endpoint:GET /items", "Lists the items.");

        var second = Scaffolder.Scaffold(Solution("/things"), withWords, Options);

        second.Report.Added.Should().Contain("endpoint:GET /things");
        second.Report.Removed.Should().Contain("endpoint:GET /items (it held text)");
        var changedHandler = Scaffolder.Scaffold(Solution(extra: "var unused = 1;").WithHandlerRenamed(), withWords, Options);
        changedHandler.Report.Stale.Should().Contain("endpoint:GET /items");
        var slot = DocumentModel.Parse(Api(changedHandler)).Blocks.Single(b => b.Id == "endpoint:GET /items").Slot!;
        slot.Should().Match<DocSlot>(s => s.Evidence == "stale" && s.Was == "inferred" && s.Text == "Lists the items.");
    }

    [Fact]
    public void AModelsWordsThatAPersonEdited_BecomeTheirs()
    {
        var first = Scaffolder.Scaffold(Solution(), new Dictionary<string, string>(), Options);
        var written = Fill(first.Files, "endpoint:GET /items", "Lists the items.");
        var edited = new Dictionary<string, string>(written) { ["docs/project/api.md"] = written["docs/project/api.md"].Replace("Lists the items.", "Lists the items, newest first.") };

        var second = Scaffolder.Scaffold(Solution(), edited, Options);

        second.Report.MarkedHuman.Should().ContainSingle("endpoint:GET /items");
        DocumentModel.Parse(Api(second)).Blocks.Single(b => b.Id == "endpoint:GET /items").Slot!.Should().Match<DocSlot>(s => s.Evidence == "human" && s.Text == "Lists the items, newest first.");
    }

    [Fact]
    public void ConfirmingAStaleSlot_MakesItHumanAndCurrent()
    {
        var first = Scaffolder.Scaffold(Solution(), new Dictionary<string, string>(), Options);
        var written = Fill(first.Files, "endpoint:GET /items", "Lists the items.");
        var stale = Scaffolder.Scaffold(Solution().WithHandlerRenamed(), written, Options);
        var item = Solution().WithHandlerRenamed().Item("endpoint:GET /items");

        var confirmed = Scaffolder.Confirm(Api(stale), "endpoint:GET /items", ItemHash.Of(item));

        DocumentModel.Parse(confirmed).Blocks.Single(b => b.Id == "endpoint:GET /items").Slot!.Should()
            .Match<DocSlot>(s => s.Evidence == "human" && s.ItemHash == ItemHash.Of(item) && s.Was == null);
    }

    [Fact]
    public void ADocumentThatIsNotWellFormed_IsNeverRewritten()
    {
        var existing = new Dictionary<string, string> { ["docs/project/api.md"] = "# API\n\n<!-- dblens:item endpoint:GET /x hash=12345678 gen=12345678 -->\n### x\n" };

        new Action(() => Scaffolder.Scaffold(Solution(), existing, Options)).Should().Throw<ArgumentException>().WithMessage("*api.md is not well formed and was not rewritten*never closed*");
    }

    [Fact]
    public void TheLanguage_ChangesTheToolsWordsOnly()
    {
        var italian = Scaffolder.Scaffold(Solution(), new Dictionary<string, string>(), new DocsOptions("it"));

        Api(italian).Should().Contain("| Verbo | Route | Handler | Dichiarato come | File |").And.Contain("Gli endpoint dichiarati nel codice.").And.Contain("endpoint:GET /items");
    }

    [Fact]
    public void WordsAPersonEdited_WhileTheItemChanged_BecomeHumanAndStaleAtOnce_NotCurrent()
    {
        var first = Scaffolder.Scaffold(Solution(), new Dictionary<string, string>(), Options).Files;
        var written = Fill(first, "endpoint:GET /items", "Lists items.");
        var edited = new Dictionary<string, string>(written) { ["docs/project/api.md"] = written["docs/project/api.md"].Replace("Lists items.", "Lists items, mine.") };

        var result = Scaffolder.Scaffold(Solution().WithAttribute("endpoint:GET /items", "handler", "Other"), edited, Options);

        result.Report.MarkedHuman.Should().Contain("endpoint:GET /items");
        result.Report.Stale.Should().Contain("endpoint:GET /items");
        DocumentModel.Parse(Api(result)).Blocks.Single(b => b.Id == "endpoint:GET /items").Slot.Should()
            .Match<DocSlot>(s => s.Evidence == "stale" && s.Was == "human" && s.Text == "Lists items, mine.", "the person's words are kept, and read again");
    }

    [Fact]
    public void ANoteAfterARegionWhoseItemLeft_IsKeptAtTheEndOfTheFile_AndReported()
    {
        var first = Scaffolder.Scaffold(Solution("/a", "app.MapGet(\"/b\", List);"), new Dictionary<string, string>(), Options).Files;
        var withNote = new Dictionary<string, string>(first) { ["docs/project/api.md"] = first["docs/project/api.md"].Replace("<!-- dblens:end -->\n\n<!-- dblens:item endpoint:GET /b", "<!-- dblens:end -->\n\nA note about /a.\n\n<!-- dblens:item endpoint:GET /b") };
        withNote["docs/project/api.md"].Should().Contain("A note about /a.");

        var result = Scaffolder.Scaffold(Solution("/b"), withNote, Options);

        Api(result).Should().Contain("A note about /a.");
        result.Report.Removed.Should().Contain(r => r.Contains("endpoint:GET /a") && r.Contains("note was kept"));
    }

    [Fact]
    public void ADocsFolderThatIsALink_IsRefused_AndSoIsAnyFolderAboveIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dblens-docs-link-{Guid.NewGuid():N}");
        var elsewhere = Path.Combine(Path.GetTempPath(), $"dblens-docs-elsewhere-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        Directory.CreateDirectory(elsewhere);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "docs", "project"), elsewhere);

            new Action(() => DocsFolder.Write(root, new DocsOptions(), new Dictionary<string, string> { ["docs/project/api.md"] = "x" })).Should().Throw<ArgumentException>().WithMessage("*link*");
            Directory.GetFiles(elsewhere).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(Path.Combine(root, "docs", "project"));
            Directory.Delete(root, recursive: true);
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Theory]
    [InlineData("{ \"docs\": 1 }")]
    [InlineData("{ \"language\": [] }")]
    [InlineData("[1]")]
    [InlineData("{ not json")]
    public void AnInvalidSettingsFile_IsAnArgumentError_NotACrash(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), $"dblens-docs-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            System.IO.File.WriteAllText(Path.Combine(root, DocsConfig.FileName), json);

            new Action(() => DocsConfig.Load(root)).Should().Throw<ArgumentException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ADocsFolderOutsideTheRoot_IsRefused()
    {
        foreach (var folder in new[] { "../docs", "/etc/docs", "a/../../b", "" })
            new Action(() => DocsFolder.Resolve("/work/repo", new DocsOptions("en", folder))).Should().Throw<ArgumentException>(folder);
    }

    private static Dictionary<string, string> Fill(IReadOnlyDictionary<string, string> files, string id, string words)
    {
        var path = files.Keys.Single(k => DocumentModel.Parse(files[k]).Blocks.Any(b => b.Id == id));
        var parsed = DocumentModel.Parse(files[path]);
        var blocks = parsed.Blocks.Select(b => b.Id == id ? b with { Slot = b.Slot! with { Text = words, TextHash = ItemHash.OfText(words) } } : b).ToList();
        return new Dictionary<string, string>(files) { [path] = DocumentModel.Render(parsed with { Blocks = blocks }) };
    }
}

internal static class ScaffoldTestExtensions
{
    /// <summary>The same solution after the endpoint's handler is renamed: the item's hash changes, its id does not.</summary>
    public static ProjectInventory WithHandlerRenamed(this ProjectInventory inventory) => inventory with
    {
        Items = [.. inventory.Items.Select(i => i.Id == "endpoint:GET /items"
            ? i with { Attributes = new SortedDictionary<string, string>(i.Attributes.ToDictionary(a => a.Key, a => a.Key == "handler" ? "ListV2" : a.Value), StringComparer.Ordinal) }
            : i)],
    };
}
