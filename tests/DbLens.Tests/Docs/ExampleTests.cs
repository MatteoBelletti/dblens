using DbLens.Docs.Cli;
using FluentAssertions;

namespace DbLens.Tests.Docs;

/// <summary>The example in the repository is what the tool writes today: a change to the generated text must regenerate it.</summary>
public sealed class ExampleTests
{
    private static (int Exit, string Output) Run(params string[] args)
    {
        StringWriter output = new(), error = new();
        var exit = DocsCli.Run(args, output, error, DocsFixtures.ExampleFolder);
        return (exit, output.ToString() + error);
    }

    [Fact]
    public void TheExample_PassesTheStrictestCheck()
    {
        var (exit, output) = Run("check", "--strict", "--require-slots");

        exit.Should().Be(0, output);
        output.Should().Contain("0 errors, 0 warnings.");
    }

    [Fact]
    public void ScaffoldingTheExample_ChangesNothing()
    {
        var before = Directory.GetFiles(Path.Combine(DocsFixtures.ExampleFolder, "docs", "project")).ToDictionary(f => f, File.ReadAllText);
        var copy = Path.Combine(Path.GetTempPath(), $"dblens-docs-example-{Guid.NewGuid():N}");
        try
        {
            foreach (var file in Directory.GetFiles(DocsFixtures.ExampleFolder, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(copy, Path.GetRelativePath(DocsFixtures.ExampleFolder, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }

            StringWriter output = new(), error = new();
            DocsCli.Run(["scaffold"], output, error, copy).Should().Be(0, error.ToString());

            output.ToString().Should().Contain("0 added, 0 changed, 0 removed, 0 slots stale, 0 slots marked human");
            foreach (var (path, text) in before)
                File.ReadAllText(Path.Combine(copy, Path.GetRelativePath(DocsFixtures.ExampleFolder, path))).Should().Be(text);
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }
}
