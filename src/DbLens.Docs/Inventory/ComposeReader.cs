using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace DbLens.Docs.Inventory;

/// <param name="BuildDirectory">The folder the service is built from (its Dockerfile's, else its context), relative to the root; null for an image only.</param>
/// <param name="Kind"><c>broker</c>, <c>database</c>, <c>cache</c>, <c>gateway</c>, <c>observability</c> or <c>service</c>, guessed from the image name.</param>
/// <param name="Mentions">Other services of the same files whose name appears as the host of a URL in its environment. Only the names: no value is kept.</param>
internal sealed record ComposeService(
    string Name, string File, string? BuildDirectory, string Kind, IReadOnlyList<string> DependsOn, IReadOnlyList<string> Mentions);

/// <summary>
/// The services of <c>docker-compose*.yml</c> files: what each is built from, what it depends on, and which other
/// services its environment points at. An environment value can be a secret and is never kept: it is matched against
/// the names of the services and dropped.
/// </summary>
internal static partial class ComposeReader
{
    public static bool IsCompose(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return (name.StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase) || name.StartsWith("compose", StringComparison.OrdinalIgnoreCase))
               && (name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"://(?:[^@/\s]*@)?([A-Za-z0-9._-]+)")]
    private static partial Regex UrlHost();

    private const int MaxBytes = 512 * 1024;
    private const int MaxDepth = 64;

    // YamlStream.Load is recursive: a file nested far enough would overflow the stack, which nothing can catch.
    private static bool ShallowEnough(string text)
    {
        var depth = 0;
        var parser = new Parser(new StringReader(text));
        while (parser.MoveNext())
            switch (parser.Current)
            {
                case SequenceStart or MappingStart when ++depth > MaxDepth:
                    return false;
                case SequenceEnd or MappingEnd:
                    depth--;
                    break;
            }

        return true;
    }

    public static (IReadOnlyList<ComposeService> Services, int Unreadable) Read(IReadOnlyList<SourceFile> files)
    {
        // Compose files are used together (-f a -f b): a service may depend on one that another file defines.
        List<(string File, YamlMappingNode Section)> sections = [];
        var unreadable = 0;
        foreach (var file in files.Where(f => IsCompose(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            try
            {
                if (file.Text.Length > MaxBytes || !ShallowEnough(file.Text))
                {
                    unreadable++;
                    continue;
                }

                YamlStream stream = [];
                stream.Load(new StringReader(file.Text));
                if (stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root && Child(root, "services") is YamlMappingNode section)
                    sections.Add((file.Path, section));
            }
            catch (YamlException)
            {
                unreadable++;
            }
        }

        var names = sections.SelectMany(s => s.Section.Children.Select(c => (c.Key as YamlScalarNode)?.Value)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<ComposeService> services = [];
        foreach (var (file, section) in sections)
            foreach (var (key, value) in section.Children)
            {
                if ((key as YamlScalarNode)?.Value is not { } name || value is not YamlMappingNode service) continue;

                var image = (Child(service, "image") as YamlScalarNode)?.Value ?? "";
                services.Add(new ComposeService(
                    name, file, BuildDirectoryOf(file, service), KindOf(image),
                    [.. DependsOn(Child(service, "depends_on")).Where(names.Contains).Order(StringComparer.Ordinal)],
                    [.. EnvironmentHosts(Child(service, "environment")).Where(h => names.Contains(h) && !h.Equals(name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)]));
            }

        // One service may be spread over several files (a base and an override): the override often has no image.
        var merged = services.OrderBy(s => s.File, StringComparer.Ordinal).GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ComposeService(
                g.First().Name, g.First().File, g.Select(s => s.BuildDirectory).FirstOrDefault(b => b is not null),
                g.Select(s => s.Kind).FirstOrDefault(k => k != "service") ?? "service",
                [.. g.SelectMany(s => s.DependsOn).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                [.. g.SelectMany(s => s.Mentions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]))
            .OrderBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal);
        return ([.. merged], unreadable);
    }

    // A key of the mapping, or of what it merges in (<<: *common).
    private static YamlNode? Child(YamlMappingNode node, string key, int depth = 0)
    {
        if (node.Children.FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == key).Value is { } own) return own;
        if (depth > 4 || node.Children.FirstOrDefault(c => (c.Key as YamlScalarNode)?.Value == "<<").Value is not { } merged) return null;

        IEnumerable<YamlNode> sources = merged is YamlSequenceNode sequence ? sequence.Children : [merged];
        return sources.OfType<YamlMappingNode>().Select(s => Child(s, key, depth + 1)).FirstOrDefault(c => c is not null);
    }

    private static string? BuildDirectoryOf(string composeFile, YamlMappingNode service)
    {
        var folder = PathText.Directory(composeFile);
        switch (Child(service, "build"))
        {
            case YamlScalarNode { Value: { Length: > 0 } context }:
                return PathText.Combine(folder, context);
            case YamlMappingNode build:
                var contextDirectory = PathText.Combine(folder, (Child(build, "context") as YamlScalarNode)?.Value ?? ".");
                return (Child(build, "dockerfile") as YamlScalarNode)?.Value is { Length: > 0 } dockerfile
                    ? PathText.Directory(PathText.Combine(contextDirectory, dockerfile))
                    : contextDirectory;
            default:
                return null;
        }
    }

    private static IEnumerable<string> DependsOn(YamlNode? node) => node switch
    {
        YamlSequenceNode sequence => sequence.Children.OfType<YamlScalarNode>().Select(s => s.Value).OfType<string>(),
        YamlMappingNode mapping => mapping.Children.Select(c => (c.Key as YamlScalarNode)?.Value).OfType<string>(),
        _ => [],
    };

    private static IEnumerable<string> EnvironmentHosts(YamlNode? node)
    {
        IEnumerable<string> values = node switch
        {
            YamlMappingNode mapping => mapping.Children.Select(c => (c.Value as YamlScalarNode)?.Value).OfType<string>(),
            YamlSequenceNode sequence => sequence.Children.OfType<YamlScalarNode>().Select(s => s.Value).OfType<string>(),
            _ => [],
        };
        return values.SelectMany(v => UrlHost().Matches(v).Select(m => m.Groups[1].Value));
    }

    private static readonly char[] TokenSeparators = ['/', ':', '-', '_', '.', '@'];

    // What a service is, from the words of its image (a service built from a project has none, and is a service).
    private static string KindOf(string image)
    {
        var tokens = image.ToLowerInvariant().Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return tokens switch
        {
            _ when tokens.Overlaps(["rabbitmq", "kafka", "activemq", "nats", "mosquitto"]) => "broker",
            _ when tokens.Overlaps(["redis", "memcached", "valkey"]) => "cache",
            _ when tokens.Overlaps(["postgres", "postgresql", "mssql", "sqlserver", "mysql", "mariadb", "mongo", "mongodb", "cassandra"]) => "database",
            _ when tokens.Overlaps(["jaeger", "zipkin", "prometheus", "grafana", "elasticsearch", "kibana", "logstash", "filebeat", "seq", "otel"]) => "observability",
            _ when tokens.Overlaps(["nginx", "traefik", "envoy"]) => "gateway",
            _ => "service",
        };
    }
}

/// <summary>Relative paths with '/', as the inventory keeps them.</summary>
internal static class PathText
{
    public static string Directory(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    /// <summary>"a/b" plus "../c/./d" is "a/c/d"; a leading "./" or "." is nothing.</summary>
    public static string Combine(string folder, string relative)
    {
        List<string> parts = [.. folder.Split('/', StringSplitOptions.RemoveEmptyEntries)];
        foreach (var part in relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (part != ".") parts.Add(part);
        return string.Join('/', parts);
    }
}
