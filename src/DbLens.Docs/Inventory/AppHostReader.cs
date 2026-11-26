using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbLens.Docs.Inventory;

/// <param name="Kind"><c>project</c>, or what the resource is (<c>broker</c>, <c>database</c>, <c>cache</c>, or the Aspire method's name).</param>
internal sealed record AppHostResource(string Name, string Kind, string? ProjectId);

internal sealed record AppHostRead(IReadOnlyList<AppHostResource> Resources, IReadOnlyList<(string From, string To)> References);

/// <summary>
/// The resources of an Aspire AppHost and which one references which: <c>builder.AddProject&lt;Projects.X&gt;("name")</c>,
/// or a helper that takes the name and the folder (<c>AddDotnetProject("name", "../X")</c>), and <c>.WithReference(other)</c>
/// down the chain. Nothing is run: it is read as syntax, and what it does not name stays out.
/// </summary>
internal static class AppHostReader
{
    public static AppHostRead Read(IReadOnlyList<ParsedFile> orchestratorFiles, IReadOnlyList<InventoryItem> projects)
    {
        var byName = projects.GroupBy(p => p.Name, StringComparer.Ordinal).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var byDirectory = projects.GroupBy(p => PathText.Directory(p.Source.File), StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        Dictionary<string, AppHostResource> resources = new(StringComparer.Ordinal);
        Dictionary<string, string> variables = new(StringComparer.Ordinal);
        List<(string From, string To)> references = [];

        foreach (var file in orchestratorFiles.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            foreach (var node in file.Root.DescendantNodes())
            {
                if (node is InvocationExpressionSyntax call && Resource(call, file.Path, byName, byDirectory) is { } resource)
                    resources.TryAdd(resource.Name, resource);

                ExpressionSyntax? chain = node switch
                {
                    VariableDeclaratorSyntax { Initializer.Value: { } init } => init,
                    ExpressionStatementSyntax { Expression: InvocationExpressionSyntax statement } => statement,
                    _ => null,
                };
                if (chain is null || ResourceOf(chain, variables) is not { } owner) continue;

                if (node is VariableDeclaratorSyntax declarator) variables[declarator.Identifier.Text] = owner;
                for (var cur = chain; cur is InvocationExpressionSyntax inv; cur = (inv.Expression as MemberAccessExpressionSyntax)?.Expression)
                    if (ParsedFile.CalledName(inv) == "WithReference" && inv.ArgumentList.Arguments.FirstOrDefault()?.Expression is IdentifierNameSyntax id && variables.TryGetValue(id.Identifier.Text, out var target))
                        references.Add((owner, target));
            }
        }

        return new AppHostRead([.. resources.Values.OrderBy(r => r.Name, StringComparer.Ordinal)], [.. references.Distinct().OrderBy(r => r.From, StringComparer.Ordinal).ThenBy(r => r.To, StringComparer.Ordinal)]);
    }

    private static string? FirstLiteral(InvocationExpressionSyntax call) =>
        call.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax l ? l.Token.ValueText : null;

    // The resource a chain is about: the first Add… call met going in from the outside creates one, otherwise it is
    // the variable the chain starts from (webApp.WithReference(x) is about webApp, postgres.AddDatabase("db") about db).
    private static string? ResourceOf(ExpressionSyntax expression, Dictionary<string, string> variables)
    {
        var cur = expression;
        while (cur is InvocationExpressionSyntax inv)
        {
            if (ParsedFile.CalledName(inv) is { } name && name.StartsWith("Add", StringComparison.Ordinal) && FirstLiteral(inv) is { } literal) return literal;
            if (inv.Expression is not MemberAccessExpressionSyntax member) return null;
            if (member.Expression is IdentifierNameSyntax id && variables.TryGetValue(id.Identifier.Text, out var known)) return known;
            cur = member.Expression;
        }

        return null;
    }

    private static AppHostResource? Resource(InvocationExpressionSyntax call, string file, Dictionary<string, string> byName, Dictionary<string, string> byDirectory)
    {
        var method = ParsedFile.CalledName(call);
        if (method is null || !method.StartsWith("Add", StringComparison.Ordinal) || FirstLiteral(call) is not { } name) return null;

        if (call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.Text: "AddProject" } generic } && generic.TypeArgumentList.Arguments.FirstOrDefault()?.ToString() is { } type)
            return new AppHostResource(name, "project", byName.GetValueOrDefault(type.Replace("Projects.", "", StringComparison.Ordinal).Replace('_', '.')));

        var path = call.ArgumentList.Arguments.Skip(1).Select(a => (a.Expression as LiteralExpressionSyntax)?.Token.ValueText).FirstOrDefault(v => v is not null && (v.StartsWith("../", StringComparison.Ordinal) || v.StartsWith("./", StringComparison.Ordinal)));
        if (path is not null)
            return new AppHostResource(name, "project", byDirectory.GetValueOrDefault(PathText.Combine(PathText.Directory(file), path)));

        return new AppHostResource(name, KindOf(method), null);
    }

    private static string KindOf(string method) => method[3..] switch
    {
        "Redis" or "Valkey" or "Garnet" => "cache",
        "RabbitMQ" or "Kafka" or "NATS" or "AzureServiceBus" => "broker",
        "Postgres" or "SqlServer" or "MongoDB" or "MySql" or "Oracle" or "Database" or "Cosmos" => "database",
        var other => other.ToLowerInvariant(),
    };
}
