using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbLens.Docs.Inventory;

/// <summary>External HTTP clients: <c>AddHttpClient</c> registrations, with the base address when the code writes it.</summary>
internal static class HttpClientExtractor
{
    private sealed record Raw(string Name, bool NameResolved, string BaseAddress, string BaseAddressFrom, SourceLocation At, string Project);

    public static (IReadOnlyList<InventoryItem> Items, IReadOnlyList<CoverageNote> Coverage) Extract(
        IReadOnlyList<ParsedFile> files, Func<string, string> projectOf)
    {
        List<Raw> raws = [];
        foreach (var file in files)
        {
            var constants = Constants.Of(file);
            foreach (var call in file.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!IsAddHttpClient(call, out var generic)) continue;

                var (name, resolved) = NameOf(call, generic, constants);
                var (address, from) = BaseAddressOf(call, constants);
                raws.Add(new Raw(name, resolved, address, from, file.Locate(call), projectOf(file.Path)));
            }
        }

        List<InventoryItem> items = [];
        foreach (var byName in raws.GroupBy(r => r.Name, StringComparer.Ordinal))
        {
            var many = byName.Select(r => r.Project).Distinct(StringComparer.Ordinal).Count() > 1;
            foreach (var byProject in byName.GroupBy(r => many ? r.Project : "", StringComparer.Ordinal))
            {
                var ordered = byProject.OrderBy(r => r.At.File, StringComparer.Ordinal).ThenBy(r => r.At.Line).ToList();
                var first = ordered[0];
                var id = $"http:{first.Name}" + (many ? $" [{first.Project}]" : "");
                items.Add(new InventoryItem(
                    id, ItemKind.HttpClient, first.Name, first.NameResolved ? ItemEvidence.Extracted : ItemEvidence.Unresolved, first.At,
                    new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["baseAddress"] = first.BaseAddress,
                        ["baseAddressFrom"] = first.BaseAddressFrom,
                        ["project"] = first.Project,
                    },
                    [.. ordered.Skip(1).Select(r => r.At)],
                    first.NameResolved ? null : "the client name is not written as text"));
            }
        }

        var noAddress = raws.Count(r => r.BaseAddressFrom == "none");
        List<CoverageNote> coverage = [];
        if (noAddress > 0)
            coverage.Add(new CoverageNote("http clients", "HTTP clients whose base address is not written as text (a variable, a call, or set elsewhere).", noAddress));
        return (items, coverage);
    }

    private static bool IsAddHttpClient(InvocationExpressionSyntax call, out GenericNameSyntax? generic)
    {
        generic = null;
        switch (call.Expression)
        {
            case MemberAccessExpressionSyntax { Name: GenericNameSyntax g } when g.Identifier.Text == "AddHttpClient":
                generic = g;
                return true;
            case MemberAccessExpressionSyntax { Name: IdentifierNameSyntax id }:
                return id.Identifier.Text == "AddHttpClient";
            case GenericNameSyntax g2 when g2.Identifier.Text == "AddHttpClient":
                generic = g2;
                return true;
            case IdentifierNameSyntax id2:
                return id2.Identifier.Text == "AddHttpClient";
            default:
                return false;
        }
    }

    private static (string Name, bool Resolved) NameOf(InvocationExpressionSyntax call, GenericNameSyntax? generic, IReadOnlyDictionary<string, string> constants)
    {
        if (generic is { TypeArgumentList.Arguments.Count: > 0 } g) return (TypeNames.Simple(g.TypeArgumentList.Arguments[0].ToString()), true);
        var first = call.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        if (first is null) return ("(default)", true);
        var text = RouteText.Of(first, constants, out _);
        return text is null ? ($"(computed at {call.SyntaxTree.FilePath}:{call.GetLocation().GetLineSpan().StartLinePosition.Line + 1})", false) : (text, true);
    }

    // BaseAddress is assigned inside the lambda of AddHttpClient or of a ConfigureHttpClient chained to it.
    private static (string Address, string From) BaseAddressOf(InvocationExpressionSyntax call, IReadOnlyDictionary<string, string> constants)
    {
        var node = (Microsoft.CodeAnalysis.SyntaxNode)call;
        while (node.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax up }) node = up;

        foreach (var assignment in node.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>())
        {
            var left = assignment.Left switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
                IdentifierNameSyntax i => i.Identifier.Text,
                _ => "",
            };
            if (left != "BaseAddress") continue;

            var argument = assignment.Right switch
            {
                ObjectCreationExpressionSyntax o => o.ArgumentList?.Arguments.FirstOrDefault()?.Expression,
                ImplicitObjectCreationExpressionSyntax o => o.ArgumentList.Arguments.FirstOrDefault()?.Expression,
                _ => null,
            };
            if (argument is null) continue;

            if (RouteText.Of(argument, constants, out _) is { } text) return (text, "code");
            if (ConfigExtractor.KeyOfRead(argument, constants) is { } key) return ($"configuration key {key}", "configuration");
        }

        return ("", "none");
    }
}

/// <summary>Classes that run in the background: derived (even through other classes of the solution) from BackgroundService or implementing IHostedService.</summary>
internal static class HostedServiceExtractor
{
    private static readonly HashSet<string> Roots = new(StringComparer.Ordinal) { "BackgroundService", "IHostedService", "IHostedLifecycleService" };

    public static IReadOnlyList<InventoryItem> Extract(IReadOnlyList<ParsedFile> files)
    {
        List<(ParsedFile File, TypeDeclarationSyntax Type, List<string> Bases)> types = [];
        foreach (var file in files)
            foreach (var type in file.Root.DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax))
                types.Add((file, type, [.. (type.BaseList?.Types ?? []).Select(b => TypeNames.Simple(b.Type.ToString()))]));

        var byName = types.GroupBy(t => t.Type.Identifier.Text, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        Dictionary<string, bool> memo = new(StringComparer.Ordinal);

        bool IsHosted(string name, int depth)
        {
            if (Roots.Contains(name)) return true;
            if (depth > 16 || !byName.TryGetValue(name, out var declarations)) return false;
            if (memo.TryGetValue(name, out var known)) return known;
            memo[name] = false; // a cycle ends here
            return memo[name] = declarations.Any(d => d.Bases.Any(b => IsHosted(b, depth + 1)));
        }

        var registered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
            foreach (var call in file.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var generic = (call.Expression as MemberAccessExpressionSyntax)?.Name as GenericNameSyntax ?? call.Expression as GenericNameSyntax;
                if (generic is { Identifier.Text: "AddHostedService", TypeArgumentList.Arguments.Count: 1 } g)
                    registered.Add(TypeNames.Simple(g.TypeArgumentList.Arguments[0].ToString()));
                else if ((call.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text == "AddHostedService")
                    foreach (var created in call.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
                        registered.Add(TypeNames.Simple(created.Type.ToString()));
            }

        List<InventoryItem> items = [];
        foreach (var group in types.Where(t => IsHosted(t.Type.Identifier.Text, 0))
                     .OrderBy(t => t.File.Path, StringComparer.Ordinal).ThenBy(t => t.File.LineOf(t.Type))
                     .GroupBy(t => Display(t.Type), StringComparer.Ordinal))
        {
            var first = group.First();
            var name = first.Type.Identifier.Text;
            items.Add(new InventoryItem(
                $"hosted:{group.Key}", ItemKind.HostedService, group.Key, ItemEvidence.Extracted, first.File.Locate(first.Type),
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["abstract"] = first.Type.Modifiers.Any(SyntaxKind.AbstractKeyword) ? "yes" : "no",
                    ["bases"] = string.Join(", ", first.Bases.Order(StringComparer.Ordinal)),
                    ["registeredWithAddHostedService"] = registered.Contains(name) ? "yes" : "no",
                },
                [.. group.Skip(1).Select(t => t.File.Locate(t.Type))], null));
        }

        return items;
    }

    private static string Display(TypeDeclarationSyntax type) =>
        type.TypeParameterList is { } parameters ? $"{type.Identifier.Text}<{string.Join(", ", parameters.Parameters.Select(p => p.Identifier.Text))}>" : type.Identifier.Text;

}

internal static class TypeNames
{
    /// <summary>"Ns.Outer.Client&lt;T&gt;" as "Client": the name a registration or a base list is matched by.</summary>
    public static string Simple(string type)
    {
        var generic = type.IndexOf('<', StringComparison.Ordinal);
        var bare = generic >= 0 ? type[..generic] : type;
        return bare[(bare.LastIndexOf('.') + 1)..];
    }
}

internal static class Constants
{
    /// <summary>The string constants of a file, by name: the only kind a route or key may be taken from.</summary>
    public static Dictionary<string, string> Of(ParsedFile file)
    {
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in file.Root.DescendantNodes().OfType<FieldDeclarationSyntax>().Where(f => f.Modifiers.Any(SyntaxKind.ConstKeyword)))
            foreach (var declarator in field.Declaration.Variables)
                if (declarator.Initializer?.Value is LiteralExpressionSyntax { Token.Value: string text })
                    constants[declarator.Identifier.Text] = text;
        return constants;
    }
}
