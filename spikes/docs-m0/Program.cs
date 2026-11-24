using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// M0 spike: projects and endpoints from syntax only. Nothing is compiled, loaded or run.
var root = Path.GetFullPath(args[0]);
var outJson = args.Length > 1 ? args[1] : null;
var sw = Stopwatch.StartNew();

static bool Skipped(string path) =>
    path.Split(Path.DirectorySeparatorChar).Any(s => s is "bin" or "obj" or "node_modules" or ".git");

var csprojs = Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories).Where(p => !Skipped(p)).Order().ToList();
var csFiles = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(p => !Skipped(p)).Order().ToList();

// ── Projects ────────────────────────────────────────────────────────────────
var central = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var props in Directory.EnumerateFiles(root, "Directory.Packages.props", SearchOption.AllDirectories))
    foreach (var pv in XDocument.Load(props).Descendants("PackageVersion"))
        if (pv.Attribute("Include")?.Value is { } id && pv.Attribute("Version")?.Value is { } v) central[id] = v;

int projReferences = 0, projReferencesResolved = 0, packages = 0, packagesInFile = 0, packagesCentral = 0, packagesUnresolved = 0;
int outputExplicit = 0, tfmExplicit = 0, sdkKnown = 0;
List<string> projectIssues = [];
foreach (var csproj in csprojs)
{
    var doc = XDocument.Load(csproj);
    var rel = Path.GetRelativePath(root, csproj);
    if (doc.Root?.Attribute("Sdk")?.Value is { Length: > 0 }) sdkKnown++;
    if (doc.Descendants("OutputType").Any()) outputExplicit++;
    if (doc.Descendants("TargetFramework").Concat(doc.Descendants("TargetFrameworks")).Any()) tfmExplicit++;
    foreach (var r in doc.Descendants("ProjectReference"))
    {
        projReferences++;
        var inc = r.Attribute("Include")?.Value?.Replace('\\', '/');
        var target = inc is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(csproj)!, inc));
        if (target is not null && !inc!.Contains("$(") && File.Exists(target)) projReferencesResolved++;
        else projectIssues.Add($"{rel}: ProjectReference {inc} not resolved");
    }
    foreach (var p in doc.Descendants("PackageReference"))
    {
        packages++;
        var id = p.Attribute("Include")?.Value ?? p.Attribute("Update")?.Value ?? "?";
        var ver = p.Attribute("Version")?.Value ?? p.Element("Version")?.Value;
        if (ver is not null && !ver.Contains("$(")) packagesInFile++;
        else if (ver is null && central.ContainsKey(id)) packagesCentral++;
        else { packagesUnresolved++; projectIssues.Add($"{rel}: PackageReference {id} version {(ver ?? "(none)")}"); }
    }
}

// ── Endpoints ───────────────────────────────────────────────────────────────
string[] verbs = ["MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch"];
HashSet<string> passThrough = ["HasApiVersion", "HasDeprecatedApiVersion", "MapToApiVersion", "WithTags", "WithOpenApi", "RequireAuthorization",
    "AllowAnonymous", "WithMetadata", "DisableAntiforgery", "NewVersionedApi", "WithName", "WithGroupName", "ReportApiVersions", "RequireCors", "WithSummary", "WithDescription", "ExcludeFromDescription", "AddEndpointFilter", "RequireRateLimiting", "WithApiVersionSet", "Produces", "ProducesProblem", "WithOrder"];

var trees = csFiles.Select(f => (File: f, Tree: CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f))).ToList();
var consts = new Dictionary<string, string>();
foreach (var (_, tree) in trees)
    foreach (var f in tree.GetRoot().DescendantNodes().OfType<FieldDeclarationSyntax>().Where(f => f.Modifiers.Any(SyntaxKind.ConstKeyword)))
        foreach (var d in f.Declaration.Variables)
            if (d.Initializer?.Value is LiteralExpressionSyntax { Token.Value: string s }) consts[d.Identifier.Text] = s;


var endpoints = new List<Endpoint>();
var rootBuilders = new HashSet<string> { "app", "endpoints", "builder", "routes", "endpointRouteBuilder" };

string? RouteOf(ExpressionSyntax? e, out string? reason)
{
    reason = null;
    switch (e)
    {
        case LiteralExpressionSyntax { Token.Value: string s }: return s;
        case InterpolatedStringExpressionSyntax i when i.Contents.All(c => c is InterpolatedStringTextSyntax):
            return string.Concat(i.Contents.Cast<InterpolatedStringTextSyntax>().Select(c => c.TextToken.Text));
        case IdentifierNameSyntax id when consts.TryGetValue(id.Identifier.Text, out var c): return c;
        case MemberAccessExpressionSyntax m when consts.TryGetValue(m.Name.Identifier.Text, out var c): return c;
        case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression):
            var l = RouteOf(b.Left, out var rl); var r = RouteOf(b.Right, out var rr);
            reason = rl ?? rr; return l is null || r is null ? null : l + r;
        case null: reason = "no route argument"; return null;
        default: reason = $"route is {e.Kind()}: {Trim(e.ToString())}"; return null;
    }
}

static string Trim(string s) => s.Length > 60 ? s[..60] + "…" : s;
static string Join(string a, string b) => "/" + string.Join('/', (a + "/" + b).Split('/', StringSplitOptions.RemoveEmptyEntries));

// Resolves the prefix a builder expression carries; null with a reason when it cannot.
string? PrefixOf(ExpressionSyntax recv, SyntaxNode? method, Dictionary<string, string?> locals, Dictionary<string, string> why, int depth = 0)
{
    switch (recv)
    {
        case IdentifierNameSyntax id:
            if (locals.TryGetValue(id.Identifier.Text, out var p)) { if (p is null) why["r"] = $"variable {id.Identifier.Text} is not a resolvable group"; return p; }
            var prm = (method as BaseMethodDeclarationSyntax)?.ParameterList.Parameters.FirstOrDefault(x => x.Identifier.Text == id.Identifier.Text);
            if (prm is not null) return ExtensionPrefix((BaseMethodDeclarationSyntax)method!, why);
            if (rootBuilders.Contains(id.Identifier.Text)) return "";
            why["r"] = $"unknown receiver {id.Identifier.Text}"; return null;
        case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma } inv:
            var name = ma.Name.Identifier.Text;
            if (name == "MapGroup")
            {
                var up = PrefixOf(ma.Expression, method, locals, why, depth + 1);
                var seg = RouteOf(inv.ArgumentList.Arguments.FirstOrDefault()?.Expression, out var rs);
                if (up is null) return null;
                if (seg is null) { why["r"] = $"MapGroup {rs}"; return null; }
                return Join(up, seg);
            }
            if (passThrough.Contains(name)) return PrefixOf(ma.Expression, method, locals, why, depth + 1);
            why["r"] = $"unknown chained method {name}"; return null;
        case ParenthesizedExpressionSyntax pe: return PrefixOf(pe.Expression, method, locals, why, depth + 1);
        default: why["r"] = $"receiver is {recv.Kind()}"; return null;
    }
}

// A parameter of an extension method (MapCatalogApi(this IEndpointRouteBuilder app)): the prefix is that of its call sites.
string? ExtensionPrefix(BaseMethodDeclarationSyntax method, Dictionary<string, string> why)
{
    if (method is not MethodDeclarationSyntax md) { why["r"] = "builder parameter of a non-method"; return null; }
    var name = md.Identifier.Text;
    var sites = trees.SelectMany(t => t.Tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()
        .Where(i => i.Expression is MemberAccessExpressionSyntax m && m.Name.Identifier.Text == name)
        .Select(i => (Inv: i, t.File))).ToList();
    if (sites.Count == 0) return ""; // a builder passed in from outside the scanned code; assumed root
    HashSet<string?> found = [];
    foreach (var (inv, _) in sites)
    {
        var enclosing = ScopeOf(inv);
        var recv = ((MemberAccessExpressionSyntax)inv.Expression).Expression;
        found.Add(PrefixOf(recv, enclosing, Locals(enclosing), why));
    }
    if (found.Count == 1) return found.First();
    why["r"] = $"extension {name} is called with {found.Count} different prefixes"; return null;
}

// The method around a node, or the file's top-level statements.
SyntaxNode ScopeOf(SyntaxNode n) => (SyntaxNode?)n.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault() ?? n.SyntaxTree.GetRoot();

Dictionary<string, string?> Locals(SyntaxNode? method)
{
    var map = new Dictionary<string, string?>();
    if (method is null) return map;
    foreach (var v in method.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        if (v.Initializer?.Value is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax } init
            && (init.ToString().Contains("MapGroup") || init.ToString().Contains("NewVersionedApi")))
        {
            var why = new Dictionary<string, string>();
            map[v.Identifier.Text] = PrefixOf(init, method, map, why);
        }
    return map;
}

int notEndpoint = 0; var otherMaps = new Dictionary<string, int>();
foreach (var (file, tree) in trees)
{
    if (file.Split(Path.DirectorySeparatorChar).Any(s => s is "tests" or "e2e")) continue;
    var rootNode = tree.GetRoot();
    foreach (var inv in rootNode.DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
        if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
        var name = ma.Name.Identifier.Text;
        if (!verbs.Contains(name)) { if (name.StartsWith("Map", StringComparison.Ordinal) && name is not ("MapGroup" or "MapDefaultEndpoints" or "MapOpenApi")) { notEndpoint++; otherMaps[name] = otherMaps.GetValueOrDefault(name) + 1; } continue; }
        var line = inv.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var method = ScopeOf(inv);
        var why = new Dictionary<string, string>();
        var prefix = PrefixOf(ma.Expression, method, Locals(method), why);
        var route = RouteOf(inv.ArgumentList.Arguments.FirstOrDefault()?.Expression, out var routeWhy);
        var verb = name[3..].ToUpperInvariant();
        var rel = Path.GetRelativePath(root, file);
        if (prefix is not null && route is not null) endpoints.Add(new(verb, Join(prefix, route), true, null, rel, line, "minimal"));
        else endpoints.Add(new(verb, null, false, routeWhy ?? why.GetValueOrDefault("r") ?? "unknown", rel, line, "minimal"));
    }

    // Controllers: [Route] on the class, [Http*] on the action.
    foreach (var cls in rootNode.DescendantNodes().OfType<ClassDeclarationSyntax>())
    {
        var isController = cls.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString().StartsWith("ApiController"))
                           || cls.BaseList?.Types.Any(t => t.Type.ToString() is "ControllerBase" or "Controller") == true;
        if (!isController) continue;
        var ctrlName = cls.Identifier.Text.Replace("Controller", "");
        var baseRoute = cls.AttributeLists.SelectMany(a => a.Attributes).FirstOrDefault(a => a.Name.ToString() == "Route")?.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
        var classRoute = baseRoute is null ? "" : RouteOf(baseRoute, out _)?.Replace("[controller]", ctrlName, StringComparison.OrdinalIgnoreCase);
        foreach (var m in cls.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword)))
            foreach (var attr in m.AttributeLists.SelectMany(a => a.Attributes).Where(a => a.Name.ToString().StartsWith("Http")))
            {
                var verb = attr.Name.ToString()[4..].ToUpperInvariant();
                var template = attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                var action = template is null ? "" : RouteOf(template, out _);
                var line = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var rel = Path.GetRelativePath(root, file);
                if (baseRoute is null && template is null)
                    endpoints.Add(new(verb, null, false, "conventional routing (no [Route], no template)", rel, line, "controller"));
                else if (classRoute is not null && action is not null)
                    endpoints.Add(new(verb, Join(classRoute, action.Replace("[action]", m.Identifier.Text, StringComparison.OrdinalIgnoreCase)), true, null, rel, line, "controller"));
                else endpoints.Add(new(verb, null, false, "controller route is not a literal", rel, line, "controller"));
            }
    }
}

sw.Stop();
var resolved = endpoints.Count(e => e.Resolved);
var sb = new StringBuilder();
sb.AppendLine($"# {root}");
sb.AppendLine($"files: {csFiles.Count} .cs, {csprojs.Count} .csproj   elapsed: {sw.ElapsedMilliseconds} ms");
sb.AppendLine($"projects: sdk known {sdkKnown}/{csprojs.Count}, OutputType explicit {outputExplicit}/{csprojs.Count}, TargetFramework explicit {tfmExplicit}/{csprojs.Count}");
sb.AppendLine($"ProjectReference: resolved {projReferencesResolved}/{projReferences}");
sb.AppendLine($"PackageReference: {packages} total; version in file {packagesInFile}, central {packagesCentral}, unresolved {packagesUnresolved}");
sb.AppendLine($"endpoints (call sites Map{{Get,Post,Put,Delete,Patch}} + controller actions, tests excluded): {endpoints.Count}; resolved {resolved}; unresolved {endpoints.Count - resolved}; other Map* calls ignored {notEndpoint}");
foreach (var g in endpoints.Where(e => !e.Resolved).GroupBy(e => e.Reason).OrderByDescending(g => g.Count())) sb.AppendLine($"  unresolved x{g.Count()}: {g.Key}");
sb.AppendLine("  other Map* calls: " + string.Join(", ", otherMaps.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}")));
foreach (var i in projectIssues.Take(15)) sb.AppendLine($"  project issue: {i}");
Console.WriteLine(sb);
foreach (var e in endpoints.OrderBy(e => e.File).ThenBy(e => e.Line))
    Console.WriteLine($"{(e.Resolved ? "OK " : "-- ")}{e.Verb,-6} {(e.Route ?? "?"),-60} {e.File}:{e.Line} {e.Reason}");
if (outJson is not null) File.WriteAllText(outJson, JsonSerializer.Serialize(endpoints.OrderBy(e => e.File).ThenBy(e => e.Line), new JsonSerializerOptions { WriteIndented = true }));
