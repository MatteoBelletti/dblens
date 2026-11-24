using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbLens.Docs.Inventory;

/// <summary>An endpoint or a background service, as a place a flow can start.</summary>
internal sealed record FlowEntry(InventoryItem Item, SyntaxNode? Body, string? HostedType);

internal sealed record OverviewExtraction(IReadOnlyList<InventoryItem> Items, IReadOnlyList<CoverageNote> Coverage);

/// <summary>
/// What the overview of a solution is made of: its components, the events that cross them, the links between them
/// (events, calls, data read, references, dependencies) and the candidate flows an entry point sets in motion.
/// Everything is read from syntax and from the compose and AppHost files; a connection that is only a deduction from
/// names is <see cref="ItemEvidence.Inferred"/>.
/// </summary>
internal static partial class OverviewExtractor
{
    private const int MaxFlowDepth = 12;

    // Schemas every database has: naming one says nothing about whose it is.
    private static readonly HashSet<string> GenericSchemas = new(StringComparer.OrdinalIgnoreCase) { "dbo", "public", "main", "sys", "information_schema" };

    [GeneratedRegex(@"^(?:https?\+?https?|https?|grpc)://([A-Za-z0-9._-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ServiceUrl();

    [GeneratedRegex(@"\b(?:FROM|JOIN)\s+""?(\w+)""?\.""?(\w+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex SqlTable();

    public static OverviewExtraction Extract(
        IReadOnlyList<InventoryItem> projects, IReadOnlyList<InventoryItem> contexts, IReadOnlyList<FlowEntry> entries,
        IReadOnlyList<ParsedFile> files, IReadOnlyList<SourceFile> allFiles, InventoryOptions options)
    {
        var components = ComponentModel.Build(projects, files, options);
        var analyzed = files.Where(f => !f.Path.Contains("/Migrations/", StringComparison.Ordinal)).ToList();
        var orchestratorFiles = files.Where(f => components.Orchestrators.Contains(components.ProjectOfFile(f.Path) ?? "")).ToList();
        var analysis = new EventAnalysis([.. analyzed.Where(f => !orchestratorFiles.Contains(f))], components, options);
        var appHost = AppHostReader.Read(orchestratorFiles, projects);
        var (compose, composeUnreadable) = ComposeReader.Read(allFiles);

        Dictionary<string, SortedSet<string>> infrastructure = components.Components.ToDictionary(c => c.Name, _ => new SortedSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        Dictionary<string, string> componentOfResource = new(StringComparer.OrdinalIgnoreCase);
        List<(string Kind, string From, string To, string Via, ItemEvidence Evidence, SourceLocation At)> links = [];

        // Orchestration: AppHost resources and references.
        var appHostFile = orchestratorFiles.Select(f => f.Path).FirstOrDefault() ?? "";
        foreach (var resource in appHost.Resources.Where(r => r.ProjectId is not null))
            if (components.ComponentOfProject(resource.ProjectId!) is { } component) componentOfResource[resource.Name] = component;
        var resourceKind = appHost.Resources.ToDictionary(r => r.Name, r => r.Kind, StringComparer.Ordinal);
        foreach (var (from, to) in appHost.References)
        {
            if (!componentOfResource.TryGetValue(from, out var source)) continue;
            if (componentOfResource.TryGetValue(to, out var target))
            {
                if (source != target) links.Add(("reference", source, target, "AppHost", ItemEvidence.Extracted, new SourceLocation(appHostFile, 1)));
            }
            else if (resourceKind.TryGetValue(to, out var kind) && kind != "project") infrastructure[source].Add($"{to} ({kind})");
        }

        // Compose: services built from a project, and what they depend on or point at.
        var projectOfDirectory = projects.Where(p => !ProjectInventoryEngine.IsTestPath(p.Source.File)).GroupBy(p => PathText.Directory(p.Source.File), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        string? ProjectOfBuild(string? directory)
        {
            for (var d = directory; d is not null; d = d.Length == 0 ? null : PathText.Directory(d))
                if (projectOfDirectory.TryGetValue(d, out var id)) return id;
            return null;
        }

        foreach (var service in compose)
            if (ProjectOfBuild(service.BuildDirectory) is { } id && components.ComponentOfProject(id) is { } component)
                componentOfResource.TryAdd(service.Name, component);
        var composeByName = compose.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var service in compose.Where(s => componentOfResource.ContainsKey(s.Name)))
        {
            var source = componentOfResource[service.Name];
            foreach (var dependency in service.DependsOn)
                if (componentOfResource.TryGetValue(dependency, out var target)) { if (source != target) links.Add(("depends", source, target, "compose", ItemEvidence.Extracted, new SourceLocation(service.File, 1))); }
                else if (composeByName.TryGetValue(dependency, out var infra)) infrastructure[source].Add($"{dependency} ({infra.Kind})");
            foreach (var mention in service.Mentions)
                if (componentOfResource.TryGetValue(mention, out var target) && source != target)
                    links.Add(("call", source, target, "compose environment", ItemEvidence.Inferred, new SourceLocation(service.File, 1)));
        }

        // Calls in the code whose URL host is the name of a service.
        foreach (var file in analyzed.Where(f => !orchestratorFiles.Contains(f)))
        {
            if (components.ComponentOfFile(file.Path) is not { } source) continue;
            foreach (var literal in file.Root.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
                if (ServiceUrl().Match(literal.Token.ValueText) is { Success: true } match && componentOfResource.TryGetValue(match.Groups[1].Value, out var target) && target != source)
                {
                    var via = literal.Ancestors().OfType<InvocationExpressionSyntax>()
                        .Select(i => i.Expression switch { MemberAccessExpressionSyntax m => m.Name.Identifier.Text, SimpleNameSyntax s => s.Identifier.Text, _ => "" })
                        .FirstOrDefault(n => n is "AddHttpClient" or "AddGrpcClient" or "MapForwarder") ?? "url";
                    links.Add(("call", source, target, via, ItemEvidence.Inferred, new SourceLocation(file.Path, file.LineOf(literal))));
                }
        }

        // Data: SQL written by hand that names a schema another component's context owns.
        var schemaOwner = contexts.Where(c => c.Attributes["defaultSchema"].Length > 0)
            .Select(c => (Schema: c.Attributes["defaultSchema"], Component: components.ComponentOfFile(c.Source.File)))
            .Where(x => x.Component is not null)
            .GroupBy(x => x.Schema, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(x => x.Component).Distinct(StringComparer.Ordinal).Count() == 1 && !GenericSchemas.Contains(g.Key))
            .ToDictionary(g => g.Key, g => g.First().Component!, StringComparer.OrdinalIgnoreCase);
        foreach (var file in analyzed)
        {
            if (components.ComponentOfFile(file.Path) is not { } source) continue;
            foreach (var literal in file.Root.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
                foreach (var match in SqlTable().Matches(literal.Token.ValueText).Cast<Match>())
                    if (schemaOwner.TryGetValue(match.Groups[1].Value, out var owner) && owner != source)
                        links.Add(("data", source, owner, $"{match.Groups[1].Value}.{match.Groups[2].Value}", ItemEvidence.Inferred, new SourceLocation(file.Path, file.LineOf(literal))));
        }

        // Events.
        var consumersOf = analysis.Consumers.GroupBy(c => c.Event, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var producersOf = analysis.Producers.GroupBy(p => p.Event, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var consumer in analysis.Consumers)
            foreach (var producer in producersOf.GetValueOrDefault(consumer.Event) ?? [])
                links.Add(("event", producer.Component, consumer.Component, consumer.Event, ItemEvidence.Extracted, new SourceLocation(consumer.File, consumer.Line)));

        List<InventoryItem> items = [];
        foreach (var component in components.Components)
        {
            var first = projects.First(p => p.Id == (component.Executables.Concat(component.Libraries).First()));
            items.Add(new InventoryItem(
                $"component:{component.Name}", ItemKind.Component, component.Name, ItemEvidence.Extracted, new SourceLocation(first.Source.File, 1),
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["executables"] = string.Join(", ", component.Executables),
                    ["infrastructure"] = string.Join(", ", infrastructure.GetValueOrDefault(component.Name) ?? []),
                    ["libraries"] = string.Join(", ", component.Libraries),
                    ["role"] = component.Role,
                    ["sdk"] = component.Sdk,
                    ["usedBy"] = string.Join(", ", component.UsedBy),
                },
                [], null));
        }

        foreach (var component in components.Components.Where(c => c.Role != "shared-library"))
            items.Add(new InventoryItem(
                $"detail:{component.Name}", ItemKind.Detail, component.Name, ItemEvidence.Extracted, items.First(i => i.Id == $"component:{component.Name}").Source,
                new SortedDictionary<string, string>(StringComparer.Ordinal) { ["component"] = component.Name }, [], null));

        foreach (var name in analysis.Producers.Select(p => p.Event).Concat(analysis.Consumers.Select(c => c.Event)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var producers = producersOf.GetValueOrDefault(name) ?? [];
            var consumers = consumersOf.GetValueOrDefault(name) ?? [];
            var at = producers.Count > 0 ? new SourceLocation(producers[0].File, producers[0].Line) : new SourceLocation(consumers[0].File, consumers[0].Line);
            items.Add(new InventoryItem(
                $"event:{name}", ItemKind.Event, name, producers.Count > 0 && consumers.Count > 0 ? ItemEvidence.Extracted : ItemEvidence.Unresolved, at,
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["consumers"] = string.Join(", ", consumers.Select(c => c.Component).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                    ["handlers"] = string.Join(", ", consumers.Select(c => $"{c.Component}.{c.Handler}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                    ["producers"] = string.Join(", ", producers.Select(p => p.Component).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
                },
                [], producers.Count == 0 ? "nothing in the solution creates it: it comes from outside, or in a way syntax does not show"
                    : consumers.Count == 0 ? "nothing in the solution consumes it" : null));
        }

        foreach (var group in links.GroupBy(l => LinkId(l.Kind, l.From, l.To, l.Via), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var (kind, from, to, via, evidence, at) = group.OrderBy(l => l.At.File, StringComparer.Ordinal).ThenBy(l => l.At.Line).First();
            items.Add(new InventoryItem(
                group.Key, ItemKind.Link, $"{from} -> {to}", evidence, at,
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["from"] = from,
                    ["kind"] = kind,
                    ["to"] = to,
                    ["via"] = via,
                },
                [.. group.Select(l => l.At).Distinct().OrderBy(a => a.File, StringComparer.Ordinal).ThenBy(a => a.Line).Skip(1)], null));
        }

        // Flows: what an entry point sets in motion, hop by hop across components.
        Dictionary<(string Handler, string Event), IReadOnlyList<string>> chains = [];
        IReadOnlyList<string> ChainOf(EventConsumer consumer)
        {
            if (chains.TryGetValue((consumer.Handler, consumer.Event), out var known)) return known;
            return chains[(consumer.Handler, consumer.Event)] = [.. analysis.Reach(analysis.StartsOfHandler(consumer.Handler, consumer.Component)).Where(e => e != consumer.Event)];
        }

        var withoutStart = 0;
        var cutShort = 0;
        foreach (var entry in entries)
        {
            var entryComponent = components.ComponentOfFile(entry.Item.Source.File) ?? "";
            var starts = entry.HostedType is not null
                ? analysis.StartsOfHostedService(entry.HostedType, entryComponent)
                : entry.Body is null ? [] : [new CodeNode("(endpoint)", entry.Item.Name, entry.Item.Source.File, entryComponent, entry.Body)];
            if (starts.Count == 0)
            {
                if (entry.HostedType is null) withoutStart++;
                continue;
            }

            var origin = components.ComponentOfFile(entry.Item.Source.File);
            var direct = analysis.Reach(starts);
            if (origin is null || direct.Count == 0) continue;

            List<string> hops = [];
            HashSet<string> visited = new(direct, StringComparer.Ordinal);
            HashSet<string> involved = new([origin], StringComparer.Ordinal);
            List<(string Event, string From)> frontier = [.. direct.Select(e => (e, origin))];
            var depth = 0;
            while (frontier.Count > 0 && depth < MaxFlowDepth)
            {
                depth++;
                List<(string Event, string From)> next = [];
                foreach (var (name, from) in frontier.OrderBy(f => f.Event, StringComparer.Ordinal))
                {
                    var consumers = consumersOf.GetValueOrDefault(name) ?? [];
                    if (consumers.Count == 0) hops.Add($"{from} -> (no consumer) : {name}");
                    foreach (var consumer in consumers)
                    {
                        involved.Add(consumer.Component);
                        var hop = $"{from} -> {consumer.Component} : {name}";
                        if (!hops.Contains(hop)) hops.Add(hop);
                        foreach (var produced in ChainOf(consumer))
                            if (visited.Add(produced)) next.Add((produced, consumer.Component));
                    }
                }

                frontier = next;
            }

            if (frontier.Count > 0) cutShort++;

            items.Add(new InventoryItem(
                $"flow:{entry.Item.Id}", ItemKind.Flow, entry.Item.Name, ItemEvidence.Inferred, entry.Item.Source,
                new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["component"] = origin,
                    ["components"] = string.Join(", ", involved.Order(StringComparer.Ordinal)),
                    ["depth"] = depth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["entry"] = entry.Item.Id,
                    ["events"] = string.Join(", ", direct),
                    ["hops"] = string.Join(" | ", hops),
                },
                [], "a deduction from names: calls are followed by method name, commands and domain events by type"));
        }

        List<CoverageNote> coverage = [];
        if (components.UnusedLibraries > 0)
            coverage.Add(new CoverageNote("components", "Libraries that no executable of the solution uses are not listed.", components.UnusedLibraries));
        if (composeUnreadable > 0)
            coverage.Add(new CoverageNote("components", "Compose files that are not valid YAML were skipped.", composeUnreadable));
        var alone = items.Count(i => i.Kind == ItemKind.Event && i.Evidence == ItemEvidence.Unresolved);
        if (alone > 0)
            coverage.Add(new CoverageNote("events", "Events that nothing in the solution creates, or that nothing consumes: the other end may be outside it, or written in a way syntax does not show.", alone));
        if (items.Any(i => i.Kind == ItemKind.Flow))
        {
            coverage.Add(new CoverageNote("flows", "A flow follows calls by method name, commands and domain events by type; it stops at an interface, at a base class and at a method name that several methods carry.", items.Count(i => i.Kind == ItemKind.Flow)));
            if (cutShort > 0)
                coverage.Add(new CoverageNote("flows", $"Flows cut at {MaxFlowDepth} hops: what follows is not listed.", cutShort));
            if (withoutStart > 0)
                coverage.Add(new CoverageNote("flows", "Endpoints whose handler could not be located, so no flow starts from them.", withoutStart));
        }

        return new OverviewExtraction(items, coverage);
    }

    private static string LinkId(string kind, string from, string to, string via) => $"link:{kind} {from} -> {to} ({via})";
}
