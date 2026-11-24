using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbLens.Docs.Inventory;

internal sealed record EventSite(string Event, string Component, string File, int Line);

internal sealed record EventConsumer(string Event, string Handler, string Component, string File, int Line);

/// <summary>A method or constructor, or the body of an endpoint, as a place the code can go through.</summary>
internal sealed record CodeNode(string Type, string Method, string File, string Component, SyntaxNode Syntax);

/// <summary>
/// The events of a solution and what leads to them, from syntax only. An event is a type whose base list names one of
/// the configured event bases; it is produced where it is created (<c>new E</c>, or a producing method called with E as
/// its type argument) and consumed where a handler is registered or declared for it.
/// What an entry point or a consumed event leads to is found by walking calls by name (only a method name that one
/// method carries in the component and the libraries it shares, and not a name every collection has), the constructors of what is created, and the handlers of the commands and domain events it creates.
/// An integration event is not followed: its handlers run in another component, later.
/// </summary>
internal sealed class EventAnalysis
{
    private static readonly string[] HandlerMethods = ["Handle", "HandleAsync", "Consume"];
    private static readonly string[] HostedMethods = ["ExecuteAsync", "StartAsync"];

    // Names every collection, repository and framework type has: a call by one of them says nothing about which method it is.
    private static readonly HashSet<string> CommonNames = new(StringComparer.Ordinal)
    {
        "Add", "AddRange", "Remove", "Clear", "Contains", "Get", "Set", "Save", "SaveChanges", "SaveChangesAsync", "Delete", "Update",
        "Create", "Write", "Read", "Dispose", "ToString", "Equals", "GetHashCode", "Send", "Publish",
    };
    private const int MaxDepth = 16;

    private readonly HashSet<string> _eventKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _eventSimpleNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _producingMethods;
    private readonly HashSet<string> _sharedComponents;
    private readonly Dictionary<string, List<CodeNode>> _nodesByType = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CodeNode>> _nodesByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string Handler, string Component)>> _handlersOf = new(StringComparer.Ordinal);
    private readonly Dictionary<CodeNode, (List<CodeNode> Next, HashSet<string> Events)> _facts = [];

    public EventAnalysis(IReadOnlyList<ParsedFile> files, ComponentModel components, InventoryOptions options)
    {
        _producingMethods = [.. options.ProducingMethods];
        _sharedComponents = [.. components.Components.Where(c => c.Role == "shared-library").Select(c => c.Name)];
        var eventBases = options.EventBases.ToHashSet(StringComparer.Ordinal);

        foreach (var file in files)
            foreach (var type in file.Root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (type.BaseList?.Types.Any(b => eventBases.Contains(Last(b.Type.ToString()))) == true)
                {
                    _eventKeys.Add(type.Parent is TypeDeclarationSyntax outer ? $"{outer.Identifier.Text}.{type.Identifier.Text}" : type.Identifier.Text);
                    _eventSimpleNames.Add(type.Identifier.Text);
                }

                var owner = components.ComponentOfFile(file.Path) ?? "";
                foreach (var b in type.BaseList?.Types ?? [])
                    if (b.Type is GenericNameSyntax generic)
                        foreach (var argument in generic.TypeArgumentList.Arguments.SelectMany(TypeNames))
                            (_handlersOf.TryGetValue(argument, out var list) ? list : _handlersOf[argument] = []).Add((type.Identifier.Text, owner));

                foreach (var member in type.Members)
                    switch (member)
                    {
                        case MethodDeclarationSyntax method:
                            Add(new CodeNode(type.Identifier.Text, method.Identifier.Text, file.Path, owner, method));
                            break;
                        case ConstructorDeclarationSyntax constructor:
                            Add(new CodeNode(type.Identifier.Text, ".ctor", file.Path, owner, constructor));
                            break;
                    }

                if (type.ParameterList is { } primary) Add(new CodeNode(type.Identifier.Text, ".ctor", file.Path, owner, primary));
            }

        foreach (var list in _nodesByType.Values) list.Sort(static (a, b) => string.CompareOrdinal($"{a.File}|{a.Method}", $"{b.File}|{b.Method}"));
        Producers = ProducerSites(components);
        Consumers = ConsumerSites(files, components);
    }

    public IReadOnlyList<EventSite> Producers { get; }

    public IReadOnlyList<EventConsumer> Consumers { get; }

    private void Add(CodeNode node)
    {
        (_nodesByType.TryGetValue(node.Type, out var byType) ? byType : _nodesByType[node.Type] = []).Add(node);
        if (node.Method != ".ctor") (_nodesByName.TryGetValue(node.Method, out var byName) ? byName : _nodesByName[node.Method] = []).Add(node);
    }

    // "Account.Query.Dto" → "Dto"; the last segment only.
    private static string Last(string name) => name[(name.LastIndexOf('.') + 1)..];

    // The simple name of a type and, when it is written qualified, its last two segments ("DomainEvent.CartItemAdded").
    private static IEnumerable<string> TypeNames(TypeSyntax type) => type switch
    {
        GenericNameSyntax g => new[] { g.Identifier.Text }.Concat(g.TypeArgumentList.Arguments.SelectMany(TypeNames)),
        QualifiedNameSyntax q => TypeNames(q.Right).Concat(q.Left is QualifiedNameSyntax or IdentifierNameSyntax ? TypeNames(q.Right).Select(r => $"{RightMost(q.Left)}.{r}") : []),
        IdentifierNameSyntax i => [i.Identifier.Text],
        _ => [],
    };

    private static string RightMost(NameSyntax name) => name switch
    {
        QualifiedNameSyntax q => RightMost(q.Right),
        SimpleNameSyntax s => s.Identifier.Text,
        _ => "",
    };

    private IEnumerable<string> Instantiated(SyntaxNode node) =>
        node.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().SelectMany(o => TypeNames(o.Type))
            .Concat(node.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(i => i.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax member } ? member : i.Expression as GenericNameSyntax)
                .Where(g => g is not null && _producingMethods.Contains(g.Identifier.Text))
                .SelectMany(g => g!.TypeArgumentList.Arguments.SelectMany(TypeNames)));

    private static IEnumerable<string> Invoked(SyntaxNode node) =>
        node.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(ParsedFile.CalledName)
            .OfType<string>();

    private List<EventSite> ProducerSites(ComponentModel components)
    {
        HashSet<EventSite> sites = [];
        foreach (var node in _nodesByType.Values.SelectMany(n => n).Where(n => !_eventSimpleNames.Contains(n.Type)))
            if (components.ComponentOfFile(node.File) is { } component)
                foreach (var e in Instantiated(node.Syntax).Distinct(StringComparer.Ordinal).Where(_eventKeys.Contains))
                    sites.Add(new EventSite(e, component, node.File, node.Syntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1));
        return [.. sites.OrderBy(s => s.Event, StringComparer.Ordinal).ThenBy(s => s.Component, StringComparer.Ordinal).ThenBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line)];
    }

    private List<EventConsumer> ConsumerSites(IReadOnlyList<ParsedFile> files, ComponentModel components)
    {
        HashSet<EventConsumer> consumers = [];
        foreach (var file in files)
        {
            if (components.ComponentOfFile(file.Path) is not { } component) continue;

            foreach (var generic in file.Root.DescendantNodes().OfType<GenericNameSyntax>().Where(g => g.Identifier.Text == "AddSubscription" && g.TypeArgumentList.Arguments.Count == 2))
                if (TypeNames(generic.TypeArgumentList.Arguments[0]).FirstOrDefault(_eventKeys.Contains) is { } e)
                    consumers.Add(new EventConsumer(e, Last(generic.TypeArgumentList.Arguments[1].ToString()), component, file.Path, file.LineOf(generic)));

            foreach (var type in file.Root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                foreach (var b in type.BaseList?.Types ?? [])
                    if (b.Type is GenericNameSyntax { Identifier.Text: "IConsumer" or "Consumer" or "IIntegrationEventHandler" } g && g.TypeArgumentList.Arguments.Count == 1
                        && TypeNames(g.TypeArgumentList.Arguments[0]).FirstOrDefault(_eventKeys.Contains) is { } e)
                        consumers.Add(new EventConsumer(e, type.Identifier.Text, component, file.Path, file.LineOf(type)));
        }

        return [.. consumers.OrderBy(c => c.Event, StringComparer.Ordinal).ThenBy(c => c.Component, StringComparer.Ordinal).ThenBy(c => c.Handler, StringComparer.Ordinal).ThenBy(c => c.File, StringComparer.Ordinal)];
    }

    // What code in a component can reach by name: its own code and the shared libraries'.
    private bool InScope(CodeNode from, CodeNode candidate) => candidate.Component == from.Component || _sharedComponents.Contains(candidate.Component);

    /// <summary>The start nodes of a consumer: the handling methods of its class, in its component.</summary>
    public IReadOnlyList<CodeNode> StartsOfHandler(string handlerType, string component) => StartsOf(handlerType, component, HandlerMethods);

    public IReadOnlyList<CodeNode> StartsOfHostedService(string typeName, string component) => StartsOf(typeName, component, HostedMethods);

    private IReadOnlyList<CodeNode> StartsOf(string type, string component, IReadOnlyCollection<string> methods) =>
        [.. _nodesByType.GetValueOrDefault(type)?.Where(n => n.Component == component && methods.Contains(n.Method)) ?? []];

    /// <summary>The events that code leads to, in order, counting only what happens in the same component.</summary>
    public IReadOnlyList<string> Reach(IEnumerable<CodeNode> starts)
    {
        HashSet<string> events = new(StringComparer.Ordinal);
        HashSet<CodeNode> seen = [];
        Queue<(CodeNode Node, int Depth)> todo = new(starts.Select(s => (s, 0)));
        while (todo.Count > 0)
        {
            // Breadth first, so a node is first met by its shortest path and the depth limit cuts nothing it should keep.
            var (node, depth) = todo.Dequeue();
            if (depth > MaxDepth || !seen.Add(node)) continue;

            var (next, produced) = FactsOf(node);
            events.UnionWith(produced);
            foreach (var n in next) todo.Enqueue((n, depth + 1));
        }

        return [.. events.Order(StringComparer.Ordinal)];
    }

    private (List<CodeNode> Next, HashSet<string> Events) FactsOf(CodeNode node)
    {
        if (_facts.TryGetValue(node, out var known)) return known;

        List<CodeNode> next = [];
        HashSet<string> events = new(StringComparer.Ordinal);
        foreach (var type in Instantiated(node.Syntax).Distinct(StringComparer.Ordinal))
        {
            if (_eventKeys.Contains(type))
            {
                events.Add(type);
                continue; // an integration event crosses the bus: its handlers run elsewhere, later
            }

            next.AddRange(_nodesByType.GetValueOrDefault(type)?.Where(n => n.Method == ".ctor" && InScope(node, n)) ?? []);
            foreach (var (handler, component) in _handlersOf.GetValueOrDefault(type) ?? [])
                next.AddRange(_nodesByType.GetValueOrDefault(handler)?.Where(n => n.Component == component && HandlerMethods.Contains(n.Method) && InScope(node, n)) ?? []);
        }

        foreach (var name in Invoked(node.Syntax).Distinct(StringComparer.Ordinal).Where(n => !CommonNames.Contains(n)))
        {
            if (!_nodesByName.TryGetValue(name, out var all)) continue;

            // Only a name that one method of the component, or of what it shares, carries.
            var candidates = all.Where(c => InScope(node, c)).ToList();
            if (candidates.Count == 1) next.Add(candidates[0]);
        }

        return _facts[node] = (next, events);
    }
}
