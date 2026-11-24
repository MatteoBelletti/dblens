using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbLens.Docs.Inventory;

/// <summary>An endpoint declaration as read; the id is assigned once every one is known.</summary>
internal sealed record RawEndpoint(
    string Verb, string? Route, string? Reason, SourceLocation At, string? Handler, string Style);

internal sealed record EndpointExtraction(
    IReadOnlyList<RawEndpoint> Endpoints,
    /// <summary>Other <c>Map…</c> calls that declare something this inventory does not list (gRPC, forwarders, Blazor…), by name.</summary>
    IReadOnlyDictionary<string, int> NotInventoried);

/// <summary>
/// Minimal-API and controller endpoints, from syntax. A route resolves only when its text is written in
/// the code: a literal, a concatenation of literals and constants of the same file, through
/// <c>MapGroup</c> variables, versioned-API builders and extension methods whose call sites agree.
/// Everything else is kept as an unresolved endpoint with its reason.
/// </summary>
internal sealed class EndpointExtractor
{
    private const int MaxDepth = 8;

    private static readonly string[] VerbCalls = ["MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch"];

    // Calls that return the builder they were called on, with the same prefix.
    private static readonly HashSet<string> PassThrough = new(StringComparer.Ordinal)
    {
        "HasApiVersion", "HasDeprecatedApiVersion", "MapToApiVersion", "WithTags", "WithOpenApi", "RequireAuthorization",
        "AllowAnonymous", "WithMetadata", "DisableAntiforgery", "NewVersionedApi", "WithName", "WithGroupName",
        "ReportApiVersions", "RequireCors", "WithSummary", "WithDescription", "ExcludeFromDescription", "AddEndpointFilter",
        "RequireRateLimiting", "WithApiVersionSet", "Produces", "ProducesProblem", "WithOrder",
    };

    // Map… calls that declare no endpoint of their own.
    private static readonly HashSet<string> NotDeclarations = new(StringComparer.Ordinal)
    {
        "MapGroup", "MapDefaultEndpoints", "MapOpenApi",
    };

    private static readonly HashSet<string> RootBuilders = new(StringComparer.Ordinal)
    {
        "app", "endpoints", "builder", "routes", "endpointRouteBuilder", "webApplication",
    };

    private readonly Dictionary<string, Dictionary<string, string>> _constants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(ParsedFile File, InvocationExpressionSyntax Call)>> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<SyntaxNode, Dictionary<string, List<Declared>>> _locals = [];
    private readonly HashSet<string> _declaredHere = new(StringComparer.Ordinal);

    private EndpointExtractor(IReadOnlyList<ParsedFile> files)
    {
        foreach (var file in files)
        {
            var constants = Constants.Of(file);
            _constants[file.Path] = constants;

            foreach (var method in file.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                _declaredHere.Add(method.Identifier.Text);

            foreach (var call in file.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = call.Expression switch
                {
                    MemberAccessExpressionSyntax access => access.Name.Identifier.Text,
                    IdentifierNameSyntax id => id.Identifier.Text,
                    _ => null,
                };
                if (name is null) continue;
                if (!_calls.TryGetValue(name, out var list)) _calls[name] = list = [];
                list.Add((file, call));
            }
        }
    }

    public static EndpointExtraction Extract(IReadOnlyList<ParsedFile> files)
    {
        var run = new EndpointExtractor(files);
        List<RawEndpoint> endpoints = [];
        Dictionary<string, int> other = new(StringComparer.Ordinal);

        foreach (var file in files)
        {
            run.MinimalApis(file, endpoints, other);
            run.Controllers(file, endpoints);
        }

        return new EndpointExtraction(endpoints, other);
    }

    // ── Minimal APIs ────────────────────────────────────────────────────────────

    private void MinimalApis(ParsedFile file, List<RawEndpoint> endpoints, Dictionary<string, int> other)
    {
        foreach (var call in file.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax access) continue;
            var name = access.Name.Identifier.Text;

            if (name == "MapMethods")
            {
                endpoints.AddRange(MapMethods(file, call, access));
                continue;
            }

            if (!VerbCalls.Contains(name))
            {
                if (name.StartsWith("Map", StringComparison.Ordinal) && !NotDeclarations.Contains(name) && !_declaredHere.Contains(name))
                    other[name] = other.GetValueOrDefault(name) + 1;
                continue;
            }

            endpoints.Add(Declaration(file, call, access, name[3..].ToUpperInvariant(), routeAt: 0, handlerAt: 1));
        }
    }

    private RawEndpoint Declaration(
        ParsedFile file, InvocationExpressionSyntax call, MemberAccessExpressionSyntax access, string verb, int routeAt, int handlerAt)
    {
        var scope = ScopeOf(call);
        Dictionary<string, string> why = [];
        var prefix = PrefixOf(access.Expression, scope, why, 0);
        var route = RouteText.Of(Arg(call, routeAt), _constants[file.Path], out var routeReason);
        var handler = HandlerName(Arg(call, handlerAt));
        var at = file.Locate(call);

        return prefix is not null && route is not null
            ? new RawEndpoint(verb, RouteText.Join(prefix, route), null, at, handler, "minimal")
            : new RawEndpoint(verb, null, routeReason ?? why.GetValueOrDefault("reason") ?? "the group it belongs to is not written as text", at, handler, "minimal");
    }

    private IEnumerable<RawEndpoint> MapMethods(ParsedFile file, InvocationExpressionSyntax call, MemberAccessExpressionSyntax access)
    {
        var verbs = VerbsOf(Arg(call, 1));
        if (verbs is null)
        {
            yield return new RawEndpoint("?", null, "the HTTP methods are not written as text", file.Locate(call), HandlerName(Arg(call, 2)), "minimal");
            yield break;
        }

        foreach (var verb in verbs)
            yield return Declaration(file, call, access, verb, routeAt: 0, handlerAt: 2);
    }

    private static List<string>? VerbsOf(ExpressionSyntax? e)
    {
        IEnumerable<ExpressionSyntax>? elements = e switch
        {
            CollectionExpressionSyntax c => c.Elements.OfType<ExpressionElementSyntax>().Select(x => x.Expression),
            ArrayCreationExpressionSyntax { Initializer: { } i } => i.Expressions,
            ImplicitArrayCreationExpressionSyntax { Initializer: { } i } => i.Expressions,
            _ => null,
        };
        if (elements is null) return null;

        List<string> verbs = [];
        foreach (var element in elements)
            switch (element)
            {
                case LiteralExpressionSyntax { Token.Value: string text }: verbs.Add(text.ToUpperInvariant()); break;
                case MemberAccessExpressionSyntax m: verbs.Add(m.Name.Identifier.Text.ToUpperInvariant()); break;
                default: return null;
            }
        return verbs.Count == 0 ? null : verbs;
    }

    private static string? HandlerName(ExpressionSyntax? e) => e switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        _ => null,
    };

    private static ExpressionSyntax? Arg(InvocationExpressionSyntax call, int index) =>
        call.ArgumentList.Arguments.Count > index ? call.ArgumentList.Arguments[index].Expression : null;

    // The method around a node, or the file's top-level statements.
    private static SyntaxNode ScopeOf(SyntaxNode node) =>
        (SyntaxNode?)node.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault() ?? node.SyntaxTree.GetRoot();

    /// <summary>The route prefix a builder expression carries; null, with the reason in <paramref name="why"/>, when it is not written as text.</summary>
    private string? PrefixOf(ExpressionSyntax receiver, SyntaxNode scope, Dictionary<string, string> why, int depth)
    {
        if (depth > MaxDepth)
        {
            why["reason"] = "the builder is passed through too many layers";
            return null;
        }

        switch (receiver)
        {
            case IdentifierNameSyntax id:
                var declared = Locals(scope).GetValueOrDefault(id.Identifier.Text)?.LastOrDefault(d => d.Start < id.SpanStart);
                if (declared is { } local)
                {
                    if (local.Prefix is null) why["reason"] = $"the group {id.Identifier.Text} is not written as text";
                    return local.Prefix;
                }
                if ((scope as BaseMethodDeclarationSyntax)?.ParameterList.Parameters.FirstOrDefault(p => p.Identifier.Text == id.Identifier.Text) is { } parameter)
                {
                    // The application itself is always the root; an IEndpointRouteBuilder or a RouteGroupBuilder may be a group.
                    if (parameter.Type?.ToString() is "WebApplication" or "Microsoft.AspNetCore.Builder.WebApplication") return "";
                    return BuilderParameterPrefix((BaseMethodDeclarationSyntax)scope, id.Identifier.Text, why, depth);
                }
                if (RootBuilders.Contains(id.Identifier.Text)) return "";
                why["reason"] = $"the receiver {id.Identifier.Text} is not a known builder";
                return null;

            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } call:
                var name = access.Name.Identifier.Text;
                if (name == "MapGroup")
                {
                    var up = PrefixOf(access.Expression, scope, why, depth + 1);
                    var segment = RouteText.Of(Arg(call, 0), _constants[call.SyntaxTree.FilePath], out var segmentReason);
                    if (up is null) return null;
                    if (segment is null)
                    {
                        why["reason"] = $"MapGroup: {segmentReason}";
                        return null;
                    }
                    return RouteText.Join(up, segment);
                }
                if (PassThrough.Contains(name)) return PrefixOf(access.Expression, scope, why, depth + 1);
                why["reason"] = $"the builder goes through {name}, which is not followed";
                return null;

            case ParenthesizedExpressionSyntax p:
                return PrefixOf(p.Expression, scope, why, depth + 1);

            default:
                why["reason"] = $"the receiver is a {receiver.Kind()}";
                return null;
        }
    }

    // A builder that is a parameter of a method (MapCatalogApi(this IEndpointRouteBuilder app), or MapOrders(RouteGroupBuilder g)):
    // the prefix of the argument at each call site, when they all agree. A method called from nowhere in the files read is unresolved.
    private string? BuilderParameterPrefix(BaseMethodDeclarationSyntax method, string parameter, Dictionary<string, string> why, int depth)
    {
        if (method is not MethodDeclarationSyntax declaration)
        {
            why["reason"] = "the builder is a parameter of something other than a method";
            return null;
        }

        var name = declaration.Identifier.Text;
        var parameters = declaration.ParameterList.Parameters;
        var index = parameters.IndexOf(p => p.Identifier.Text == parameter);
        var isExtension = parameters.Count > 0 && parameters[0].Modifiers.Any(SyntaxKind.ThisKeyword);

        if (!_calls.TryGetValue(name, out var sites))
        {
            why["reason"] = $"{name} is called from nowhere in the files read, so its group is not known";
            return null;
        }

        HashSet<string?> found = [];
        foreach (var (_, call) in sites)
        {
            var argument = ArgumentFor(call, index, parameters.Count, isExtension);
            if (argument is null) continue;
            found.Add(PrefixOf(argument, ScopeOf(call), why, depth + 1));
        }

        if (found.Count == 0)
        {
            why["reason"] = $"no call of {name} passes its group in a way that can be followed";
            return null;
        }

        if (found.Count == 1) return found.First();
        why["reason"] = $"{name} is called on groups with different prefixes";
        return null;
    }

    // The expression a call passes for parameter number `index`: the receiver for the first parameter of an extension method
    // called as x.M(), else the argument at the same position (shifted by one when the receiver stands for the first parameter).
    private static ExpressionSyntax? ArgumentFor(InvocationExpressionSyntax call, int index, int parameterCount, bool isExtension)
    {
        var arguments = call.ArgumentList.Arguments;
        var receiverStyle = isExtension && call.Expression is MemberAccessExpressionSyntax && arguments.Count == parameterCount - 1;
        if (receiverStyle) return index == 0 ? ((MemberAccessExpressionSyntax)call.Expression).Expression : index - 1 < arguments.Count ? arguments[index - 1].Expression : null;
        return index < arguments.Count ? arguments[index].Expression : null;
    }

    private sealed record Declared(int Start, string? Prefix);

    // The group variables of a scope, by name, each with where it is declared: a use sees the nearest declaration before it.
    private Dictionary<string, List<Declared>> Locals(SyntaxNode scope)
    {
        if (_locals.TryGetValue(scope, out var cached)) return cached;

        var map = new Dictionary<string, List<Declared>>(StringComparer.Ordinal);
        _locals[scope] = map; // before resolving, so a variable that refers to itself ends instead of looping
        foreach (var declarator in scope.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            if (declarator.Initializer?.Value is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax } init
                && init.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(i =>
                    i.Expression is MemberAccessExpressionSyntax m && m.Name.Identifier.Text is "MapGroup" or "NewVersionedApi"))
            {
                var name = declarator.Identifier.Text;
                if (!map.TryGetValue(name, out var list)) map[name] = list = [];
                list.Add(new Declared(init.Span.End, PrefixOf(init, scope, [], 1)));
            }
        return map;
    }

    // ── Controllers ─────────────────────────────────────────────────────────────

    private void Controllers(ParsedFile file, List<RawEndpoint> endpoints)
    {
        foreach (var type in file.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!IsController(type)) continue;

            var controller = type.Identifier.Text.EndsWith("Controller", StringComparison.Ordinal)
                ? type.Identifier.Text[..^"Controller".Length]
                : type.Identifier.Text;
            var classRouteExpression = AttributeArgument(type.AttributeLists, "Route");
            var constants = _constants[file.Path];
            var classRoute = classRouteExpression is null ? null : RouteText.Of(classRouteExpression, constants, out _);

            foreach (var method in type.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword)))
            {
                if (HasAttribute(method.AttributeLists, "NonAction")) continue;
                var handler = $"{type.Identifier.Text}.{method.Identifier.Text}";

                foreach (var attribute in method.AttributeLists.SelectMany(a => a.Attributes).Where(IsVerbAttribute))
                {
                    var verb = AttributeName(attribute)[4..].ToUpperInvariant();
                    var at = file.Locate(method);
                    var templateExpression = Template(attribute) ?? AttributeArgument(method.AttributeLists, "Route");
                    var template = templateExpression is null ? null : RouteText.Of(templateExpression, constants, out _);

                    if (classRouteExpression is null && templateExpression is null)
                    {
                        endpoints.Add(new RawEndpoint(verb, null, "conventional routing: no [Route] and no template", at, handler, "controller"));
                        continue;
                    }

                    if ((classRouteExpression is not null && classRoute is null) || (templateExpression is not null && template is null))
                    {
                        endpoints.Add(new RawEndpoint(verb, null, "the route template is not written as text", at, handler, "controller"));
                        continue;
                    }

                    var absolute = template is not null && (template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal));
                    var full = absolute ? template!.TrimStart('~') : RouteText.Join(classRoute ?? "", template ?? "");
                    full = full.Replace("[controller]", controller, StringComparison.OrdinalIgnoreCase)
                               .Replace("[action]", method.Identifier.Text, StringComparison.OrdinalIgnoreCase);
                    endpoints.Add(new RawEndpoint(verb, RouteText.Join("", full), null, at, handler, "controller"));
                }
            }
        }
    }

    private static bool IsController(ClassDeclarationSyntax type) =>
        HasAttribute(type.AttributeLists, "ApiController")
        || HasAttribute(type.AttributeLists, "Controller")
        || type.BaseList?.Types.Any(t => t.Type.ToString() is "ControllerBase" or "Controller") == true;

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name.ToString();
        var dot = name.LastIndexOf('.');
        name = dot >= 0 ? name[(dot + 1)..] : name;
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    private static bool IsVerbAttribute(AttributeSyntax attribute) =>
        AttributeName(attribute) is "HttpGet" or "HttpPost" or "HttpPut" or "HttpDelete" or "HttpPatch";

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string name) =>
        lists.SelectMany(l => l.Attributes).Any(a => AttributeName(a) == name);

    private static ExpressionSyntax? AttributeArgument(SyntaxList<AttributeListSyntax> lists, string name) =>
        Template(lists.SelectMany(l => l.Attributes).FirstOrDefault(a => AttributeName(a) == name));

    // The template is the first positional argument: [HttpGet(Name = "x")] and [HttpGet(Order = 1)] carry none.
    private static ExpressionSyntax? Template(AttributeSyntax? attribute) =>
        attribute?.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals is null && a.NameColon is null)?.Expression;
}
