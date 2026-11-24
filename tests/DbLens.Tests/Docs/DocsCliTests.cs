using DbLens.Docs.Cli;
using FluentAssertions;

namespace DbLens.Tests.Docs;

/// <summary>The command line end to end, on a small solution in a temporary folder.</summary>
public sealed class DocsCliTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dblens-docs-cli-");

    public void Dispose() => _root.Delete(recursive: true);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private sealed class CliResult
    {
        public int Code { get; init; }

        public string Out { get; init; } = "";

        public string Err { get; init; } = "";
    }

    private CliResult Run(params string[] args)
    {
        StringWriter output = new(), error = new();
        var code = DocsCli.Run(args, output, error, _root.FullName);
        return new CliResult { Code = code, Out = output.ToString(), Err = error.ToString() };
    }

    private void Solution(string route = "/orders")
    {
        Write("src/Api/Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write("src/Api/Orders.cs", $"public static class Orders {{ public static void Map(WebApplication app) {{ app.MapGet(\"{route}\", List); }} }}");
    }

    [Fact]
    public void Scaffold_ThenCheck_Passes_AndAChangeInTheCodeFailsTheCheck_UntilScaffoldRunsAgain()
    {
        Solution();

        Run("scaffold").Code.Should().Be(0);
        Run("check").Should().Match<CliResult>(r => r.Code == 0 && r.Out.Contains("0 errors"));

        Solution("/orders/{id}");
        var failed = Run("check");
        failed.Code.Should().Be(1);
        failed.Out.Should().Contain("missing-item").And.Contain("orphan-item").And.Contain("endpoint:GET /orders/{id}");

        Run("scaffold").Out.Should().Contain("1 added").And.Contain("removed: endpoint:GET /orders");
        Run("check").Code.Should().Be(0);
    }

    [Fact]
    public void AGeneratedRegionEditedByHand_FailsTheCheck()
    {
        Solution();
        Run("scaffold");
        var path = Path.Combine(_root.FullName, "docs/project/api.md");
        File.WriteAllText(path, File.ReadAllText(path).Replace("| GET | /orders |", "| GET | /orderz |"));

        var result = Run("check");

        result.Code.Should().Be(1);
        result.Out.Should().Contain("generated-edited");
    }

    [Fact]
    public void Check_Json_ListsTheIssues_AndStrictFailsOnStaleSlots()
    {
        Solution();
        Run("scaffold");

        var json = Run("check", "--json");

        json.Code.Should().Be(0);
        json.Out.Should().Contain("\"ok\": true").And.Contain("\"code\": \"slot-empty\"").And.Contain("\"severity\": \"warning\"");
        Run("check", "--require-slots").Code.Should().Be(1, "empty slots are errors when asked");
    }

    [Fact]
    public void TheSettingsFile_AndTheOptions_ChooseLanguageAndFolder()
    {
        Solution();
        Write("dblens-docs.json", "{ \"language\": \"it\", \"docs\": \"documentazione\" }");

        Run("scaffold").Code.Should().Be(0);
        File.ReadAllText(Path.Combine(_root.FullName, "documentazione/api.md")).Should().Contain("Gli endpoint dichiarati nel codice.");

        Run("scaffold", "--language", "en", "--docs", "other").Code.Should().Be(0);
        File.ReadAllText(Path.Combine(_root.FullName, "other/api.md")).Should().Contain("Endpoints declared in the code.");
    }

    [Fact]
    public void Confirm_MakesAStaleSlotHuman()
    {
        Solution();
        Run("scaffold");
        var path = Path.Combine(_root.FullName, "docs/project/api.md");
        File.WriteAllText(path, DocumentCheckTests.Fill(File.ReadAllText(path), "endpoint:GET /orders", "Lists the orders."));

        Run("confirm", "endpoint:GET /orders").Out.Should().Contain("confirmed as human");

        File.ReadAllText(path).Should().Contain("evidence=human");
    }

    [Fact]
    public void Inventory_PrintsTheJson_OrWritesIt()
    {
        Solution();

        Run("inventory").Out.Should().StartWith("{\n  \"schemaVersion\": 1,").And.Contain("endpoint:GET /orders");
        Run("inventory", "--out", "inv.json").Code.Should().Be(0);
        File.ReadAllText(Path.Combine(_root.FullName, "inv.json")).Should().Contain("project:Api");
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("check", "--docs", "../outside")]
    [InlineData("check", "--nope")]
    [InlineData("confirm")]
    [InlineData("check", "/no/such/folder")]
    public void BadInput_IsExitCode2_WithAMessage(params string[] args)
    {
        var result = Run(args);

        result.Code.Should().Be(2);
        result.Err.Should().StartWith("dblens-docs:");
    }

    [Fact]
    public void NoArguments_ShowTheUsage_AndHelpExitsZero()
    {
        Run().Code.Should().Be(2);
        Run("--help").Should().Match<CliResult>(r => r.Code == 0 && r.Out.Contains("scaffold"));
    }
}
