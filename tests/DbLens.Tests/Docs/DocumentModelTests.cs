using DbLens.Docs.Documents;
using FluentAssertions;

namespace DbLens.Tests.Docs;

public sealed class DocumentModelTests
{
    private static DocBlock Block(string id, string generated = "### X", DocSlot? slot = null, string trailing = "") =>
        new(DocBlockKind.Item, id, "1234abcd", ItemHash.OfText(generated), generated, slot, trailing, 0);

    [Fact]
    public void ADocumentTheToolWrote_ParsesBackToTheSameDocument()
    {
        var slot = new DocSlot("endpoint:GET /a/{id}", 2, "inferred", null, "1234abcd", ItemHash.OfText("Returns one.\nOr 404."), "Returns one.\nOr 404.");
        var document = new ParsedDocument("# API\n\nIntro written by a person.", [
            Block("endpoint:GET /a/{id}", "### GET /a/{id}\n\n| Verb |\n| --- |\n| GET |", slot, "A note a person left here."),
            Block("endpoint:POST /a", "### POST /a", new DocSlot("endpoint:POST /a", 2, "stale", "human", "00000000", "e3b0c442", "")),
        ], []);

        var text = DocumentModel.Render(document);
        var parsed = DocumentModel.Parse(text);

        parsed.Problems.Should().BeEmpty();
        parsed.Leading.Should().Be(document.Leading);
        parsed.Blocks.Select(b => (b.Id, b.Generated, b.Trailing)).Should().Equal(document.Blocks.Select(b => (b.Id, b.Generated, b.Trailing)));
        parsed.Blocks[0].Slot.Should().BeEquivalentTo(slot);
        parsed.Blocks[1].Slot.Should().Match<DocSlot>(s => s.Evidence == "stale" && s.Was == "human" && s.Text == "");
        DocumentModel.Render(parsed).Should().Be(text);
    }

    [Fact]
    public void AnIdWithDoubleDashOrAngleBracket_SurvivesTheHtmlComment()
    {
        const string id = "endpoint:GET /a--b/{x}>y [H]";
        var text = DocumentModel.Render(new ParsedDocument("", [Block(id)], []));

        text.Should().NotContain("a--b", "a double dash would end an HTML comment");
        DocumentModel.Parse(text).Blocks.Single().Id.Should().Be(id);
    }

    [Fact]
    public void CrLfFiles_ParseLikeLfFiles()
    {
        var text = DocumentModel.Render(new ParsedDocument("# T", [Block("project:A", slot: new DocSlot("project:A", 2, "inferred", null, "1234abcd", ItemHash.OfText("hi"), "hi"))], []));

        DocumentModel.Parse(text.Replace("\n", "\r\n")).Blocks.Single().Slot!.Text.Should().Be("hi");
        ItemHash.OfText("a\r\nb\r\n").Should().Be(ItemHash.OfText("a\nb"));
    }

    [Fact]
    public void AnUnclosedRegion_AndAStrayMarker_AreProblems()
    {
        DocumentModel.Parse("# T\n\n<!-- dblens:item project:A hash=12345678 gen=12345678 -->\n### A\n").Problems
            .Should().ContainSingle().Which.Should().Contain("the region of project:A is never closed");
        DocumentModel.Parse("# T\n\n<!-- dblens:slot nonsense -->\n").Problems
            .Should().ContainSingle().Which.Should().Contain("not well formed");
    }
}
