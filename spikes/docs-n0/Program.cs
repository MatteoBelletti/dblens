using System.Diagnostics;
using System.Text.RegularExpressions;
using DbLens.Docs.Inventory;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// N0 spike: macro-components, events, calls, data and event chains, from syntax only.
var root = Path.GetFullPath(args[0]);
var truthPath = args.Length > 1 ? args[1] : null;
var sw = Stopwatch.StartNew();

var read = SolutionReader.Read(root);
var inventory = ProjectInventoryEngine.Build(read);

static string Last(string text) => text[(text.LastIndexOf('.') + 1)..];
static string Segment(string path) => path[(path.LastIndexOf('/') + 1)..];
static bool IsTestPath(string path) => path.Split('/').Any(s => s.Equals("tests", StringComparison.OrdinalIgnoreCase) || s.Equals("test", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) || s.EndsWith("Tests", StringComparison.OrdinalIgnoreCase));

// ── Parse every non-test C# file once ───────────────────────────────────────
var trees = read.Files
    .Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !IsTestPath(f.Path) && !f.Path.Contains("/Migrations/", StringComparison.Ordinal))
    .Select(f => (f.Path, Root: CSharpSyntaxTree.ParseText(f.Text, path: f.Path).GetRoot()))
    .ToList();

// ── Projects and macro-components ───────────────────────────────────────────
// A project is keyed by its name, plus its folder when the name repeats (one "Application" per service is common).
static string Dir(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
var projects = inventory.Items.Where(i => i.Kind == ItemKind.Project && !IsTestPath(i.Source.File)).ToList();
var duplicated = projects.GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
string KeyOf(InventoryItem p) => duplicated.Contains(p.Name) ? $"{p.Name} [{Dir(p.Source.File)}]" : p.Name;
var keyByFile = projects.ToDictionary(p => p.Source.File, KeyOf);
var projectDirs = projects.ToDictionary(KeyOf, p => Dir(p.Source.File), StringComparer.Ordinal);
string ProjectOf(string file)
{
    string? best = null;
    foreach (var (name, dir) in projectDirs)
        if ((dir.Length == 0 || file.StartsWith(dir + "/", StringComparison.Ordinal)) && (best is null || dir.Length > projectDirs[best].Length)) best = name;
    return best ?? "(none)";
}
static string Resolve(string from, string include)
{
    List<string> parts = [.. Dir(from).Split('/', StringSplitOptions.RemoveEmptyEntries)];
    foreach (var seg in include.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); } else if (seg != ".") parts.Add(seg);
    return string.Join('/', parts);
}
int unresolvedRefs = 0;
List<string> ReferencesOf(InventoryItem p)
{
    var text = read.Files.First(f => f.Path == p.Source.File).Text;
    List<string> result = [];
    foreach (var m in Regex.Matches(text, "<ProjectReference\\s+Include=\"([^\"]+)\"", RegexOptions.IgnoreCase).Cast<Match>())
    {
        var target = Resolve(p.Source.File, m.Groups[1].Value);
        if (keyByFile.TryGetValue(target, out var key)) result.Add(key); else unresolvedRefs++;
    }
    return result;
}

var orchestrators = trees.Where(t => t.Root.ToString().Contains("DistributedApplication", StringComparison.Ordinal)).Select(t => ProjectOf(t.Path)).ToHashSet();
var refs = projects.ToDictionary(KeyOf, ReferencesOf, StringComparer.Ordinal);
var exes = projects.Where(p => p.Attributes["outputType"] == "Exe" && !orchestrators.Contains(KeyOf(p))).Select(KeyOf).Order().ToList();

HashSet<string> Closure(string start)
{
    HashSet<string> seen = [];
    Stack<string> todo = new([start]);
    while (todo.Count > 0)
        foreach (var r in refs.GetValueOrDefault(todo.Pop()) ?? [])
            if (seen.Add(r)) todo.Push(r);
    return seen;
}
var closures = exes.ToDictionary(e => e, Closure);
Dictionary<string, string> macroOf = [];
foreach (var e in exes) macroOf[e] = e;
HashSet<string> shared = [], orphans = [];
foreach (var lib in projects.Select(KeyOf).Where(n => !exes.Contains(n) && !orchestrators.Contains(n)))
{
    var users = exes.Where(e => closures[e].Contains(lib)).ToList();
    if (users.Count == 1) macroOf[lib] = users[0];
    else if (users.Count > 1) shared.Add(lib);
    else orphans.Add(lib);
}
string MacroOfFile(string file) { var p = ProjectOf(file); return macroOf.TryGetValue(p, out var m) ? Pretty(m) : shared.Contains(p) ? "(shared)" : orchestrators.Contains(p) ? "(orchestrator)" : "(none)"; }

// Display name: an executable whose name repeats is named by the folders that tell it apart ("Account/Query").
string Pretty(string key)
{
    var bracket = key.IndexOf(" [", StringComparison.Ordinal);
    if (bracket < 0) return key;
    var name = key[..bracket];
    var mine = projectDirs[key].Split('/');
    var others = projectDirs.Where(d => d.Key.StartsWith(name + " [", StringComparison.Ordinal)).Select(d => d.Value.Split('/')).ToList();
    var common = 0;
    while (common < mine.Length - 1 && others.All(o => o.Length > common && o[common] == mine[common])) common++;
    var tail = mine[common..^1];
    return tail.Length > 0 ? string.Join('/', tail) : key;
}

// ── AppHost: resource names, references ─────────────────────────────────────
Dictionary<string, string> serviceOfResource = new(StringComparer.Ordinal); // resource name -> project name
Dictionary<string, string> variableResource = new(StringComparer.Ordinal);
List<(string From, string To)> appHostRefs = [];
foreach (var (path, tree) in trees.Where(t => orchestrators.Contains(ProjectOf(t.Path))))
{
    foreach (var call in tree.DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
        var name = (call.Expression as MemberAccessExpressionSyntax)?.Name ?? call.Expression as SimpleNameSyntax;
        if (name is null) continue;
        var literals = call.ArgumentList.Arguments.Select(a => (a.Expression as LiteralExpressionSyntax)?.Token.ValueText).ToList();
        if (name is GenericNameSyntax { Identifier.Text: "AddProject" } g && g.TypeArgumentList.Arguments.FirstOrDefault()?.ToString() is { } projectType && literals.FirstOrDefault() is { } rname)
            serviceOfResource[rname] = projectDirs.Keys.FirstOrDefault(k => k == projectType.Replace("Projects.", "").Replace('_', '.')) ?? projectType;
        else if (name.Identifier.Text.StartsWith("Add", StringComparison.Ordinal) && literals.Count >= 2 && literals[0] is { } r2 && literals[1] is { } pathArg && pathArg.StartsWith("../", StringComparison.Ordinal) && projectDirs.Any(d => Segment(d.Value) == Segment(pathArg)))
            serviceOfResource[r2] = projectDirs.First(d => Segment(d.Value) == Segment(pathArg)).Key;
    }
    foreach (var decl in tree.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        if (decl.Initializer?.Value is { } init && ChainRoot(init) is { } root0 && FirstLiteral(root0) is { } res) variableResource[decl.Identifier.Text] = res;
    foreach (var stmt in tree.DescendantNodes().OfType<ExpressionStatementSyntax>().Where(s => s.Expression is InvocationExpressionSyntax))
        if (ChainRoot(stmt.Expression) is { } r && FirstLiteral(r) is { } res0) Chain(stmt.Expression, res0);
    foreach (var decl in tree.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        if (decl.Initializer?.Value is { } init2 && variableResource.TryGetValue(decl.Identifier.Text, out var res1)) Chain(init2, res1);
}
static InvocationExpressionSyntax? ChainRoot(ExpressionSyntax e)
{
    InvocationExpressionSyntax? last = null;
    var cur = e;
    while (cur is InvocationExpressionSyntax inv) { last = inv; cur = (inv.Expression as MemberAccessExpressionSyntax)?.Expression ?? inv.Expression; }
    return last;
}
static string? FirstLiteral(InvocationExpressionSyntax inv) => inv.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax l ? l.Token.ValueText : null;
void Chain(ExpressionSyntax e, string resource)
{
    var cur = e;
    while (cur is InvocationExpressionSyntax inv)
    {
        if ((inv.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text == "WithReference" && inv.ArgumentList.Arguments.FirstOrDefault()?.Expression is IdentifierNameSyntax id && variableResource.TryGetValue(id.Identifier.Text, out var target))
            appHostRefs.Add((resource, target));
        cur = (inv.Expression as MemberAccessExpressionSyntax)?.Expression ?? inv.Expression;
    }
}
var resourceMacro = serviceOfResource.ToDictionary(p => p.Key, p => macroOf.GetValueOrDefault(p.Value, p.Value), StringComparer.Ordinal);

// ── Types, methods, handlers, events ────────────────────────────────────────
// Preset: the base types that mark an event (the eShop bus and the MassTransit-style contracts).
HashSet<string> eventBases = new(StringComparer.Ordinal) { "IntegrationEvent", "IEvent", "IDomainEvent" };
HashSet<string> eventTypes = [];
foreach (var (_, tree) in trees)
    foreach (var t in tree.DescendantNodes().OfType<TypeDeclarationSyntax>())
        if (t.BaseList?.Types.Any(b => eventBases.Contains(Last(b.Type.ToString()))) == true)
            eventTypes.Add(t.Parent is TypeDeclarationSyntax outer ? $"{outer.Identifier.Text}.{t.Identifier.Text}" : t.Identifier.Text);

static IEnumerable<string> TypeNames(TypeSyntax type) => type switch
{
    GenericNameSyntax g => new[] { g.Identifier.Text }.Concat(g.TypeArgumentList.Arguments.SelectMany(TypeNames)),
    QualifiedNameSyntax q => TypeNames(q.Right).Concat(q.Left is QualifiedNameSyntax or IdentifierNameSyntax ? TypeNames(q.Right).Select(r => $"{Right(q.Left)}.{r}") : []),
    IdentifierNameSyntax i => [i.Identifier.Text],
    _ => [],
};
static string Right(NameSyntax n) => n switch { QualifiedNameSyntax q => Right(q.Right), SimpleNameSyntax s => s.Identifier.Text, _ => "" };

List<Node> nodes = [];
Dictionary<string, List<string>> handlersOf = new(StringComparer.Ordinal); // type name -> handler classes
Dictionary<string, int> methodNameCount = new(StringComparer.Ordinal);
foreach (var (path, tree) in trees)
    foreach (var t in tree.DescendantNodes().OfType<TypeDeclarationSyntax>())
    {
        foreach (var b in t.BaseList?.Types ?? [])
            if (b.Type is GenericNameSyntax gen)
                foreach (var arg in gen.TypeArgumentList.Arguments.SelectMany(TypeNames))
                    (handlersOf.TryGetValue(arg, out var l) ? l : handlersOf[arg] = []).Add(t.Identifier.Text);
        foreach (var m in t.Members)
            switch (m)
            {
                case MethodDeclarationSyntax md: nodes.Add(new Node(t.Identifier.Text, md.Identifier.Text, path, md)); methodNameCount[md.Identifier.Text] = methodNameCount.GetValueOrDefault(md.Identifier.Text) + 1; break;
                case ConstructorDeclarationSyntax cd: nodes.Add(new Node(t.Identifier.Text, ".ctor", path, cd)); break;
            }
        if (t.ParameterList is not null) nodes.Add(new Node(t.Identifier.Text, ".ctor", path, t));
    }
var nodesByType = nodes.GroupBy(n => n.Type).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
var nodesByName = nodes.Where(n => n.Method != ".ctor").GroupBy(n => n.Method).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

// Preset: the generic methods that raise or send an event. A generic call that merely names one (a registration) does not produce it.
HashSet<string> producingMethods = new(StringComparer.Ordinal) { "RaiseEvent", "Publish", "PublishAsync", "Send", "AddAndSaveEventAsync" };
IEnumerable<string> Instantiated(SyntaxNode n) =>
    n.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().SelectMany(o => TypeNames(o.Type))
        .Concat(n.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax mg } ? mg : i.Expression as GenericNameSyntax)
            .Where(g => g is not null && producingMethods.Contains(g.Identifier.Text))
            .SelectMany(g => g!.TypeArgumentList.Arguments.SelectMany(TypeNames)));
IEnumerable<string> Invoked(SyntaxNode n) => n.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(i => (i.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text ?? (i.Expression as IdentifierNameSyntax)?.Identifier.Text ?? (i.Expression as GenericNameSyntax)?.Identifier.Text ?? "").Where(s => s.Length > 0);

int ambiguousCalls = 0;
IEnumerable<Node> Next(Node n)
{
    if (n.Syntax is null) yield break;
    foreach (var type in Instantiated(n.Syntax).Distinct())
    {
        foreach (var c in nodesByType.GetValueOrDefault(type)?.Where(x => x.Method == ".ctor") ?? []) yield return c;
        // An integration event crosses the bus: its handlers run elsewhere, later. Only commands and domain events are followed.
        if (eventTypes.Contains(type)) continue;
        foreach (var h in handlersOf.GetValueOrDefault(type) ?? [])
            foreach (var hn in nodesByType.GetValueOrDefault(h)?.Where(x => x.Method == "Handle") ?? []) yield return hn;
    }
    foreach (var name in Invoked(n.Syntax).Distinct())
    {
        if (!nodesByName.TryGetValue(name, out var candidates)) continue;
        if (candidates.Count == 1) yield return candidates[0];
        else ambiguousCalls++;
    }
}
HashSet<string> Reach(IEnumerable<Node> starts, out int visited)
{
    HashSet<string> events = [];
    HashSet<Node> seen = [];
    Stack<(Node N, int D)> todo = new(starts.Select(s => (s, 0)));
    while (todo.Count > 0)
    {
        var (n, d) = todo.Pop();
        if (!seen.Add(n) || d > 16) continue;
        foreach (var t in Instantiated(n.Syntax!)) if (eventTypes.Contains(t)) events.Add(t);
        foreach (var x in Next(n)) todo.Push((x, d + 1));
    }
    visited = seen.Count;
    return events;
}

// producers: where an event type is instantiated (outside its own declaration)
HashSet<string> producers = [];
foreach (var n in nodes.Where(n => n.Syntax is not null && !eventTypes.Contains(n.Type)))
    foreach (var t in Instantiated(n.Syntax!).Distinct().Where(eventTypes.Contains)) producers.Add($"{t}@{MacroOfFile(n.File)}");

// consumers: AddSubscription<E, H>
HashSet<string> subscriptions = [];
List<(string Event, string Handler, string Macro)> consumers = [];
foreach (var (path, tree) in trees)
    foreach (var g in tree.DescendantNodes().OfType<GenericNameSyntax>().Where(g => g.Identifier.Text == "AddSubscription" && g.TypeArgumentList.Arguments.Count == 2))
    {
        var e = g.TypeArgumentList.Arguments[0].ToString(); var h = g.TypeArgumentList.Arguments[1].ToString();
        subscriptions.Add($"{e}@{MacroOfFile(path)}"); consumers.Add((e, h, MacroOfFile(path)));
    }

foreach (var (path, tree) in trees)
    foreach (var t in tree.DescendantNodes().OfType<TypeDeclarationSyntax>())
        foreach (var b in t.BaseList?.Types ?? [])
            if (b.Type is GenericNameSyntax { Identifier.Text: "IConsumer" or "Consumer" or "IIntegrationEventHandler" } g && g.TypeArgumentList.Arguments.Count == 1)
                foreach (var key in TypeNames(g.TypeArgumentList.Arguments[0]).Where(eventTypes.Contains).Take(1))
                { subscriptions.Add($"{key}@{MacroOfFile(path)}"); consumers.Add((key, t.Identifier.Text, MacroOfFile(path))); }

// event edges: producer macro -> consumer macro, by event name
HashSet<string> eventEdges = [];
foreach (var c in consumers)
    foreach (var p in producers.Where(p => p.StartsWith(c.Event + "@", StringComparison.Ordinal)))
        eventEdges.Add($"{p[(p.IndexOf('@') + 1)..]}->{c.Macro} {c.Event}");

// chains: consumed event -> events produced by what its handler reaches
HashSet<string> chains = [];
foreach (var c in consumers)
{
    var start = nodesByType.GetValueOrDefault(Last(c.Handler))?.Where(x => x.Method == "Handle").ToList() ?? [];
    foreach (var produced in Reach(start, out _))
        if (produced != c.Event) chains.Add($"{c.Event}->{produced}");
}

// entry points: endpoints, hosted services
HashSet<string> entries = [];
foreach (var ep in inventory.Items.Where(i => i.Kind == ItemKind.Endpoint && i.Attributes["handler"].Length > 0))
{
    var h = Last(ep.Attributes["handler"]);
    var starts = nodes.Where(n => n.Method == h && n.File == ep.Source.File).ToList();
    if (starts.Count == 0 && nodesByName.TryGetValue(h, out var all) && all.Count == 1) starts = all;
    foreach (var e in Reach(starts, out _)) entries.Add($"{h}->{e}");
}
foreach (var hs in inventory.Items.Where(i => i.Kind == ItemKind.HostedService))
{
    var starts = nodesByType.GetValueOrDefault(hs.Name.Split('<')[0])?.Where(x => x.Method is "ExecuteAsync" or "StartAsync").ToList() ?? [];
    foreach (var e in Reach(starts, out _)) entries.Add($"{hs.Name}->{e}");
}
var lambdaEndpoints = inventory.Items.Count(i => i.Kind == ItemKind.Endpoint && i.Attributes["handler"].Length == 0);

// synchronous calls: URLs whose host is an AppHost resource
HashSet<string> sync = [];
var urlRx = new Regex(@"^(?:https?\+?https?|https?|grpc)://([a-z0-9-]+)", RegexOptions.IgnoreCase);
foreach (var (path, tree) in trees.Where(t => !orchestrators.Contains(ProjectOf(t.Path))))
    foreach (var lit in tree.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
        if (urlRx.Match(lit.Token.ValueText) is { Success: true } m && resourceMacro.TryGetValue(m.Groups[1].Value, out var target))
        {
            var kind = lit.Ancestors().OfType<InvocationExpressionSyntax>().Select(i => (i.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text ?? (i.Expression as GenericNameSyntax)?.Identifier.Text).FirstOrDefault(n => n is not null and ("AddHttpClient" or "AddGrpcClient" or "MapForwarder")) ?? "other";
            sync.Add($"{MacroOfFile(path)}->{target} {kind}");
        }

// data: SQL literals naming schema.table that a context owns
HashSet<string> data = [];
var schemaOwner = inventory.Items.Where(i => i.Kind == ItemKind.Context && i.Attributes["defaultSchema"].Length > 0).ToDictionary(i => i.Attributes["defaultSchema"], i => MacroOfFile(i.Source.File), StringComparer.OrdinalIgnoreCase);
var sqlRx = new Regex(@"\b(?:FROM|JOIN)\s+""?(\w+)""?\.""?(\w+)""?", RegexOptions.IgnoreCase);
foreach (var (path, tree) in trees)
    foreach (var lit in tree.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
        foreach (Match m in sqlRx.Matches(lit.Token.ValueText))
            if (schemaOwner.TryGetValue(m.Groups[1].Value, out var owner) && owner != MacroOfFile(path)) data.Add($"{MacroOfFile(path)}->{owner} {m.Groups[1].Value}.{m.Groups[2].Value}");

// ── Report ──────────────────────────────────────────────────────────────────
Console.WriteLine($"root: {root}  ({sw.Elapsed.TotalSeconds:F1} s, {trees.Count} C# files, {nodes.Count} method nodes)");
Console.WriteLine($"orchestrators: {string.Join(", ", orchestrators)}; project references not resolved to a project: {unresolvedRefs}");
Console.WriteLine($"macros ({exes.Count}): {string.Join(", ", exes.Select(Pretty))}");
foreach (var e in exes) { var priv = macroOf.Where(p => p.Value == e && p.Key != e).Select(p => p.Key).Order().ToList(); if (priv.Count > 0) Console.WriteLine($"  {e} <- {string.Join(", ", priv)}"); }
Console.WriteLine($"shared: {string.Join(", ", shared.Order())}");
Console.WriteLine($"orphans: {string.Join(", ", orphans.Order())}");
Console.WriteLine($"appHost resources: {string.Join(", ", serviceOfResource.Select(p => $"{p.Key}={p.Value}").Order())}");
Console.WriteLine($"appHost references: {string.Join("; ", appHostRefs.Select(r => $"{r.From}->{r.To}").Distinct().Order())}");
Console.WriteLine($"event types: {eventTypes.Count}; producers: {producers.Count}; subscriptions: {subscriptions.Count}; event edges: {eventEdges.Count}; chains: {chains.Count}; entries: {entries.Count}; sync: {sync.Count}; data: {data.Count}; ambiguous calls skipped: {ambiguousCalls}; lambda endpoints unresolved: {lambdaEndpoints}");

Dictionary<string, HashSet<string>> found = new()
{
    ["macros"] = [.. exes], ["shared"] = [.. shared], ["orphan"] = [.. orphans],
    ["private"] = [.. exes.SelectMany(e => macroOf.Where(p => p.Value == e && p.Key != e).Select(p => $"{e}={p.Key}"))],
    ["producers"] = producers, ["subscriptions"] = subscriptions, ["eventedges"] = eventEdges, ["chains"] = chains, ["entries"] = entries, ["sync"] = sync, ["data"] = data,
};
if (truthPath is null) { foreach (var (k, v) in found) { Console.WriteLine($"\n[{k}]"); foreach (var x in v.Order()) Console.WriteLine("  " + x); } return; }

Dictionary<string, HashSet<string>> truth = [];
string? section = null;
foreach (var line in File.ReadAllLines(truthPath).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
    if (line.StartsWith('[')) truth[section = line.Trim('[', ']')] = [];
    else truth[section!].Add(line);
Console.WriteLine($"\n{"section",-14}{"truth",6}{"found",7}{"hit",5}{"missed",8}{"extra",7}");
foreach (var (k, expected) in truth)
{
    var got = found.GetValueOrDefault(k) ?? [];
    var hit = expected.Intersect(got).Count();
    Console.WriteLine($"{k,-14}{expected.Count,6}{got.Count,7}{hit,5}{expected.Count - hit,8}{got.Count - hit,7}");
    foreach (var m in expected.Except(got).Order()) Console.WriteLine($"    MISSED {m}");
    foreach (var x in got.Except(expected).Order()) Console.WriteLine($"    EXTRA  {x}");
}

sealed record Node(string Type, string Method, string File, MemberDeclarationSyntax? Syntax)
{
    public string Key => $"{Type}.{Method}";
}
