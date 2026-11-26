using System.Text.RegularExpressions;
using DbLens.Docs.Inventory;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbLens.Docs.Documents;

/// <summary>A place in the code a slot cites, in backticks: <c>Apis/OrdersApi.cs: CreateOrderAsync</c> or <c>OrderingContext: SaveChangesAsync</c>.</summary>
/// <param name="Where">A file, or the end of its path, or the name of a type.</param>
public sealed record CodeReference(string Where, string Member)
{
    public override string ToString() => $"{Where}: {Member}";
}

/// <summary>
/// What the words of a slot cite and whether the code still holds it. A citation is a claim the code can answer: the file or
/// type is there and declares that member. The hash of what is cited tells when the code changed under the words.
/// </summary>
public sealed partial class ProseReferences(IReadOnlyList<SourceFile> sources)
{
    [GeneratedRegex(@"`(?<where>[\w./\\-]+\.cs|[A-Z]\w*(?:\.[A-Z]\w*)*)\s*:\s*(?<member>[A-Za-z_]\w*)`")]
    private static partial Regex CitationRx();

    private readonly Dictionary<string, SyntaxNode> _parsed = new(StringComparer.Ordinal);
    private readonly Dictionary<CodeReference, List<string>> _resolved = [];

    public static IReadOnlyList<CodeReference> Extract(string text) =>
        [.. CitationRx().Matches(text).Select(m => new CodeReference(m.Groups["where"].Value.Replace('\\', '/'), m.Groups["member"].Value)).Distinct()];

    /// <summary>The citations that name nothing in the code, and the hash of the code of those that do (null when none does).</summary>
    public (IReadOnlyList<CodeReference> Missing, string? Hash) Check(string text)
    {
        List<CodeReference> missing = [];
        List<string> found = [];
        foreach (var reference in Extract(text).OrderBy(r => r.ToString(), StringComparer.Ordinal))
        {
            var code = Resolve(reference);
            if (code.Count == 0) missing.Add(reference);
            else found.Add($"{reference}\n{string.Join('\n', code)}");
        }

        return (missing, found.Count == 0 ? null : ItemHash.OfText(string.Join("\n--\n", found)));
    }

    // A slot, the check and the listing cite the same places again and again: each is looked up once.
    private List<string> Resolve(CodeReference reference)
    {
        if (!_resolved.TryGetValue(reference, out var code)) _resolved[reference] = code = ResolveUncached(reference);
        return code;
    }

    private List<string> ResolveUncached(CodeReference reference)
    {
        var isFile = reference.Where.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        var type = isFile ? null : reference.Where[(reference.Where.LastIndexOf('.') + 1)..];
        var typeRx = type is null ? null : new Regex($@"\b(?:class|record|struct|interface|enum)\s+{Regex.Escape(type)}\b");

        List<string> code = [];
        foreach (var file in sources.Where(s => isFile
                     ? s.Path.Equals(reference.Where, StringComparison.OrdinalIgnoreCase) || s.Path.EndsWith("/" + reference.Where, StringComparison.OrdinalIgnoreCase)
                     : s.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && s.Text.Contains(type!, StringComparison.Ordinal) && typeRx!.IsMatch(s.Text)))
        {
            if (!_parsed.TryGetValue(file.Path, out var root)) _parsed[file.Path] = root = CSharpSyntaxTree.ParseText(file.Text).GetRoot();
            code.AddRange(root.DescendantNodes().Where(n => Declares(n, reference.Member, type)).Select(Cited));
        }

        return code;
    }

    // Anything a person can name in a sentence about a type: its callable members, and the data it holds.
    private static bool Declares(SyntaxNode node, string member, string? type) =>
        Names(node, member) && (type is null || Owner(node) == type);

    private static bool Names(SyntaxNode node, string member) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text == member,
        ConstructorDeclarationSyntax c => c.Identifier.Text == member,
        LocalFunctionStatementSyntax l => l.Identifier.Text == member,
        PropertyDeclarationSyntax p => p.Identifier.Text == member,
        EventDeclarationSyntax e => e.Identifier.Text == member,
        VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax or EventFieldDeclarationSyntax } v => v.Identifier.Text == member,
        EnumMemberDeclarationSyntax e => e.Identifier.Text == member,
        // A primary constructor is the type itself, and its parameters are what it takes.
        TypeDeclarationSyntax { ParameterList: not null } t => t.Identifier.Text == member,
        ParameterSyntax { Parent.Parent: TypeDeclarationSyntax } p => p.Identifier.Text == member,
        _ => false,
    };

    // What is cited of a type with a primary constructor is the constructor, not the whole type; line endings are not code.
    private static string Cited(SyntaxNode node) =>
        (node is TypeDeclarationSyntax { ParameterList: { } parameters } ? parameters : node).NormalizeWhitespace().ToFullString().Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string? Owner(SyntaxNode node) =>
        node is TypeDeclarationSyntax { ParameterList: not null } t
            ? t.Identifier.Text
            : node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
}
