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
/// What the words of a slot cite and whether the code still holds it. A citation is a claim the code can answer: the file, or a
/// type the sources declare, is there and declares that member (or a base type in the sources does). The hash of what is cited
/// tells when the code changed under the words.
/// </summary>
public sealed partial class ProseReferences(IReadOnlyList<SourceFile> sources)
{
    [GeneratedRegex(@"`(?<where>[\w./\\-]+\.cs|[A-Z]\w*(?:\.[A-Z]\w*)*)\s*:\s*(?<member>[A-Za-z_]\w*)`")]
    private static partial Regex CitationRx();

    private readonly Dictionary<string, SyntaxNode> _parsed = new(StringComparer.Ordinal);
    private readonly Dictionary<CodeReference, (List<string> Code, bool Unverifiable)> _resolved = [];
    private readonly Dictionary<string, List<BaseTypeDeclarationSyntax>> _types = new(StringComparer.Ordinal);

    public static IReadOnlyList<CodeReference> Extract(string text) =>
        [.. CitationRx().Matches(text).Select(m => new CodeReference(m.Groups["where"].Value.Replace('\\', '/'), m.Groups["member"].Value)).Distinct()];

    /// <summary>
    /// The citations that name nothing in the code, and the hash of the code of those that do (null when none does). A citation
    /// of a type the sources do not declare (a framework type, or a header written like one) is neither: nothing can answer it.
    /// </summary>
    public (IReadOnlyList<CodeReference> Missing, string? Hash) Check(string text)
    {
        List<CodeReference> missing = [];
        List<string> found = [];
        foreach (var reference in Extract(text).OrderBy(r => r.ToString(), StringComparer.Ordinal))
        {
            var (code, unverifiable) = Resolve(reference);
            if (code.Count > 0) found.Add($"{reference}\n{string.Join('\n', code)}");
            else if (!unverifiable) missing.Add(reference);
        }

        return (missing, found.Count == 0 ? null : ItemHash.OfText(string.Join("\n--\n", found)));
    }

    // A slot, the check and the listing cite the same places again and again: each is looked up once.
    private (List<string> Code, bool Unverifiable) Resolve(CodeReference reference)
    {
        if (!_resolved.TryGetValue(reference, out var resolution)) _resolved[reference] = resolution = ResolveUncached(reference);
        return resolution;
    }

    private (List<string> Code, bool Unverifiable) ResolveUncached(CodeReference reference)
    {
        if (reference.Where.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            List<string> inFile = [];
            foreach (var file in sources.Where(s => s.Path.Equals(reference.Where, StringComparison.OrdinalIgnoreCase) || s.Path.EndsWith("/" + reference.Where, StringComparison.OrdinalIgnoreCase)))
                inFile.AddRange(Root(file).DescendantNodes().Where(n => Names(n, reference.Member)).Select(Cited));
            return (inFile, false);
        }

        var type = reference.Where[(reference.Where.LastIndexOf('.') + 1)..];
        var declarations = Declarations(type);
        if (declarations.Count == 0) return ([], true);

        var code = Members(declarations, type, reference.Member, 0);
        return (code, code.Count == 0 && declarations.Any(d => d.BaseList is not null));
    }

    private SyntaxNode Root(SourceFile file)
    {
        if (!_parsed.TryGetValue(file.Path, out var root)) _parsed[file.Path] = root = CSharpSyntaxTree.ParseText(file.Text).GetRoot();
        return root;
    }

    private List<BaseTypeDeclarationSyntax> Declarations(string type)
    {
        if (_types.TryGetValue(type, out var known)) return known;

        var rx = new Regex($@"\b(?:class|record|struct|interface|enum)\s+{Regex.Escape(type)}\b");
        return _types[type] =
        [
            .. sources.Where(s => s.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && s.Text.Contains(type, StringComparison.Ordinal) && rx.IsMatch(s.Text))
                .SelectMany(s => Root(s).DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(t => t.Identifier.Text == type)),
        ];
    }

    // What the type declares, then what a base type declared in the sources does: a member may be inherited from one.
    private List<string> Members(List<BaseTypeDeclarationSyntax> declarations, string type, string member, int depth)
    {
        List<string> code = [.. declarations.SelectMany(d => d.DescendantNodes().Prepend(d).Where(n => Names(n, member) && Owner(n) == type)).Select(Cited)];
        if (code.Count == 0 && member == type) return ["default constructor"];
        if (code.Count > 0 || depth >= MaxBaseDepth) return code;

        foreach (var name in declarations.SelectMany(d => d.BaseList?.Types ?? []).Select(b => b.Type.ToString().Split('.').Last().Split('<')[0]).Distinct(StringComparer.Ordinal))
            if (Declarations(name) is { Count: > 0 } bases) code.AddRange(Members(bases, name, member, depth + 1));
        return code;
    }

    private const int MaxBaseDepth = 5;

    // Anything a person can name in a sentence about a type: its callable members, and the data it holds.
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

    // What is cited is its tokens: spacing, comments and line endings are not code. A field is cited with its type and modifiers,
    // a primary constructor by its parameters.
    private static string Cited(SyntaxNode node)
    {
        var cited = node switch
        {
            TypeDeclarationSyntax { ParameterList: { } parameters } => parameters,
            VariableDeclaratorSyntax { Parent.Parent: { } declaration } => declaration,
            _ => node,
        };
        return string.Join(' ', cited.DescendantTokens().Select(t => t.Text)).Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string? Owner(SyntaxNode node) =>
        node is TypeDeclarationSyntax { ParameterList: not null } t
            ? t.Identifier.Text
            : node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
}
