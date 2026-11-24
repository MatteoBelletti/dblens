using DbLens.Docs.Inventory;
using FluentAssertions;

namespace DbLens.Tests.Docs;

public sealed class SolutionReaderTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dblens-docs-");

    public void Dispose() => _root.Delete(recursive: true);

    private void Write(string relative, string text, long? padTo = null)
    {
        var path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, padTo is { } n ? text + new string(' ', (int)n) : text);
    }

    [Fact]
    public void OnlyTheKindsOfFileTheInventoryNeeds_AreOpened_WithRelativeSlashedPaths()
    {
        Write("src/App/App.csproj", "<Project />");
        Write("src/App/Program.cs", "class A {}");
        Write("src/App/appsettings.Production.json", "{}");
        Write("src/App/secrets.json", "{ \"Key\": \"s3cret\" }");
        Write("src/App/.env", "TOKEN=abc");
        Write("src/App/notes.txt", "x");
        Write("Directory.Packages.props", "<Project />");

        var read = SolutionReader.Read(_root.FullName);

        read.Files.Select(f => f.Path).Should().Equal(
            "Directory.Packages.props", "src/App/App.csproj", "src/App/Program.cs", "src/App/appsettings.Production.json");
        read.Files.Should().NotContain(f => f.Text.Contains("s3cret") || f.Text.Contains("TOKEN"));
    }

    [Fact]
    public void BuildOutputDependenciesAndGit_AreSkipped()
    {
        foreach (var folder in new[] { "bin", "obj", "node_modules", ".git", ".vs" }) Write($"src/App/{folder}/Generated.cs", "class G {}");
        Write("src/App/Real.cs", "class R {}");

        SolutionReader.Read(_root.FullName).Files.Select(f => f.Path).Should().Equal("src/App/Real.cs");
    }

    [Fact]
    public void ALargeFile_IsNotRead_AndSaysSo()
    {
        Write("src/App/Big.cs", "class B {}", padTo: SolutionReader.MaxFileBytes);
        Write("src/App/Small.cs", "class S {}");

        var read = SolutionReader.Read(_root.FullName);

        read.Files.Select(f => f.Path).Should().Equal("src/App/Small.cs");
        read.Coverage.Should().ContainSingle(c => c.Area == "reading" && c.Count == 1);
    }

    [SkippableFact]
    public void ALinkIsNotFollowed()
    {
        var outside = Directory.CreateTempSubdirectory("dblens-docs-outside-");
        try
        {
            File.WriteAllText(Path.Combine(outside.FullName, "Leak.cs"), "class L {}");
            Write("src/App/Real.cs", "class R {}");
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(_root.FullName, "src", "link"), outside.FullName);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Skip.If(true, "This machine does not let the test create a symbolic link (Windows without developer mode).");
            }

            SolutionReader.Read(_root.FullName).Files.Select(f => f.Path).Should().Equal("src/App/Real.cs");
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public void ASnapshotThatCannotBeRead_IsInCoverage_NotAFailure()
    {
        Write("src/App/Migrations/BrokenModelSnapshot.cs", "class NotASnapshot { }");

        var read = SolutionReader.Read(_root.FullName);

        read.Models.Should().BeEmpty();
        read.Coverage.Should().ContainSingle(c => c.Area == "data model" && c.Note.Contains("BrokenModelSnapshot.cs was not read"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/no/such/folder/anywhere")]
    public void ARootThatIsNotAFolder_IsAnError(string root) =>
        new Action(() => SolutionReader.Read(root)).Should().Throw<ArgumentException>().WithMessage("root must be an existing folder.");
}
