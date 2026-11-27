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
    [GeneratedRegex(@"`(?<where>[\w./\\-]+\.(?:cs|razor)|[A-Z]\w*(?:\.[A-Z]\w*)*)\s*:\s*(?<member>[A-Za-z_]\w*)`")]
    private static partial Regex CitationRx();

    private readonly Dictionary<string, List<SyntaxNode>> _parsed = new(StringComparer.Ordinal);
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
        if (reference.Where.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || reference.Where.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            return (InFile(reference.Where, reference.Member), false);

        var type = reference.Where[(reference.Where.LastIndexOf('.') + 1)..];
        var declarations = Declarations(type);

        // A component is a type with no declaration in C#: its file is its name.
        if (declarations.Count == 0 && sources.Any(s => s.Path.EndsWith("/" + type + ".razor", StringComparison.OrdinalIgnoreCase) || s.Path.Equals(type + ".razor", StringComparison.OrdinalIgnoreCase)))
            return (InFile(type + ".razor", reference.Member), false);
        if (declarations.Count == 0) return ([], true);

        var code = Members(declarations, type, reference.Member, 0);
        return (code, code.Count == 0 && MayInherit(declarations, 0));
    }

    private List<string> InFile(string where, string member) =>
    [
        .. sources.Where(s => s.Path.Equals(where, StringComparison.OrdinalIgnoreCase) || s.Path.EndsWith("/" + where, StringComparison.OrdinalIgnoreCase))
            .SelectMany(Roots).SelectMany(r => r.DescendantNodes()).Where(n => Names(n, member)).Select(Cited),
    ];

    private List<SyntaxNode> Roots(SourceFile file)
    {
        if (!_parsed.TryGetValue(file.Path, out var roots))
            _parsed[file.Path] = roots = file.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                ? [.. RazorCode().Matches(file.Text).Select(m => CSharpSyntaxTree.ParseText("class __Razor " + file.Text[(m.Index + m.Length - 1)..]).GetRoot())]
                : [CSharpSyntaxTree.ParseText(file.Text).GetRoot()];
        return roots;
    }

    // The C# of a component is in its @code and @functions blocks: parsed as the body of a class, from the opening brace; what
    // follows the closing brace (markup) is left over and read as nothing.
    [GeneratedRegex(@"@(?:code|functions)\s*\{")]
    private static partial Regex RazorCode();

    private List<BaseTypeDeclarationSyntax> Declarations(string type)
    {
        if (_types.TryGetValue(type, out var known)) return known;

        var rx = new Regex($@"\b(?:class|record|struct|interface|enum)\s+{Regex.Escape(type)}\b");
        return _types[type] =
        [
            .. sources.Where(s => s.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && s.Text.Contains(type, StringComparison.Ordinal) && rx.IsMatch(s.Text))
                .SelectMany(s => Roots(s).SelectMany(r => r.DescendantNodes()).OfType<BaseTypeDeclarationSyntax>().Where(t => t.Identifier.Text == type)),
        ];
    }

    // What the type declares, then what a base type declared in the sources does: a member may be inherited from one.
    private List<string> Members(List<BaseTypeDeclarationSyntax> declarations, string type, string member, int depth)
    {
        List<string> code = [.. declarations.SelectMany(d => d.DescendantNodes().Prepend(d).Where(n => Names(n, member) && Owner(n) == type)).Select(Cited)];
        if (code.Count == 0 && member == type && depth == 0 && declarations.Any(d => d is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax && !d.Modifiers.Any(SyntaxKind.StaticKeyword)))
            return ["default constructor"];
        if (code.Count > 0 || depth >= MaxBaseDepth) return code;

        foreach (var name in BaseNames(declarations))
            if (Declarations(name) is { Count: > 0 } bases) code.AddRange(Members(bases, name, member, depth + 1));
        return code;
    }

    private static IEnumerable<string> BaseNames(List<BaseTypeDeclarationSyntax> declarations) =>
        declarations.SelectMany(d => d.BaseList?.Types ?? []).Where(b => b.Type is not PredefinedTypeSyntax)
            .Select(b => b.Type.ToString().Split('<')[0].Split('.').Last()).Distinct(StringComparer.Ordinal);

    // A member that is not declared may be inherited only from a base type the sources do not show: a framework class. An
    // interface, one of the sources or the I-named kind outside them, supplies no members to the type that implements it.
    private bool MayInherit(List<BaseTypeDeclarationSyntax> declarations, int depth) =>
        depth < MaxBaseDepth && BaseNames(declarations).Any(name => Declarations(name) is { Count: > 0 } bases
            ? MayInherit(bases, depth + 1)
            : !(name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1])));

    private const int MaxBaseDepth = 5;

    // What a sentence about a type names: its methods and constructors, and the data it holds.
    private static bool Names(SyntaxNode node, string member) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text == member,
        ConstructorDeclarationSyntax c => c.Identifier.Text == member,
        PropertyDeclarationSyntax p => p.Identifier.Text == member,
        VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax } v => v.Identifier.Text == member,
        EnumMemberDeclarationSyntax e => e.Identifier.Text == member,
        // A primary constructor is the type itself.
        TypeDeclarationSyntax { ParameterList: not null } t => t.Identifier.Text == member,
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
