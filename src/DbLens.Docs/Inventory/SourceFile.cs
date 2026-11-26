using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbLens.Docs.Inventory;

/// <summary>A file of the solution as the engine reads it: path relative to the root with '/', and its text.</summary>
public sealed record SourceFile(string Path, string Text);

/// <summary>A C# file parsed once, for every extractor.</summary>
internal sealed class ParsedFile(SourceFile file)
{
    public string Path { get; } = file.Path;

    public SyntaxTree Tree { get; } = CSharpSyntaxTree.ParseText(file.Text, path: file.Path);

    public SyntaxNode Root => Tree.GetRoot();

    public int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    public SourceLocation Locate(SyntaxNode node) => new(Path, LineOf(node));

    /// <summary>The name a call goes by: <c>Add</c> for <c>x.Add(…)</c>, <c>Add&lt;T&gt;</c> and <c>Add(…)</c>.</summary>
    public static string? CalledName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax { Name: { } name } => name.Identifier.Text,
        SimpleNameSyntax name => name.Identifier.Text,
        _ => null,
    };
}
