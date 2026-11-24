using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbLens.Core.Analysis.Glossary;
using DbLens.Core.Models;
using static DbLens.Core.Analysis.Names;

namespace DbLens.Mcp.Infrastructure;

/// <summary>
/// Queries the user confirmed as correct, kept in one local JSON file and scoped to the dialect,
/// the server and the database they were certified against: two servers can hold databases of the
/// same name. An entry written before the server was recorded has none: it stays readable for any
/// server, and <see cref="LegacyCaveat"/> says what that means. Plain file, no service.
/// </summary>
public sealed class CertifiedQueryStore(string path)
{
    /// <param name="Server">"host:port" of the server it was certified on; null in an entry from before it was recorded.</param>
    public sealed record Entry(
        string Name, string Question, string Sql, DatabaseDialect Dialect, string Database, string CertifiedAt,
        string? Server = null);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _gate = new();

    public string Path { get; } = path;

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dblens", "certified-queries.json");

    /// <summary>"host:port" of a target, the key part that tells two servers apart; null when unknown.</summary>
    public static string? ServerOf(Target target) =>
        target.Host is null ? null : target.Port is { } port ? $"{target.Host}:{port}" : target.Host;

    /// <summary>The entries for this database on this server, and the legacy ones that name no server.</summary>
    public IReadOnlyList<Entry> List(DatabaseDialect dialect, string database, string? server = null)
    {
        lock (_gate)
            return [.. Load().Where(e => Applies(e, dialect, database, server)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public Entry? Find(DatabaseDialect dialect, string database, string name, string? server = null) =>
        List(dialect, database, server).FirstOrDefault(e => Same(e.Name, name));

    public Entry? Matching(DatabaseDialect dialect, string database, string sql, string? server = null) =>
        List(dialect, database, server).FirstOrDefault(e => Normalize(e.Sql) == Normalize(sql));

    /// <summary>What reusing an entry from before the server was recorded means.</summary>
    public static string LegacyCaveat(Entry entry, string? server) =>
        $"The certified query '{entry.Name}' was saved before queries were tied to a server, so it may have been " +
        "certified on another server that has a database of the same name" +
        (server is null ? "." : $"; certify it again on {server} to tie it to this one.");

    private static bool Applies(Entry e, DatabaseDialect dialect, string database, string? server) =>
        e.Dialect == dialect && Same(e.Database, database)
        && (server is null || e.Server is null || Same(e.Server, server));

    /// <summary>Adds or replaces the entry with this name for this database on this server (and a legacy one, which it ties down).</summary>
    public Entry Save(string name, string question, string sql, DatabaseDialect dialect, string database, string? server = null)
    {
        var entry = new Entry(name.Trim(), question.Trim(), sql.Trim(), dialect, database,
            DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), server);

        lock (_gate)
        {
            var all = Load()
                .Where(e => !(Applies(e, dialect, database, server) && Same(e.Name, entry.Name)))
                .Append(entry)
                .ToList();

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(all, Json));
            File.Move(temp, Path, overwrite: true);
        }

        return entry;
    }

    public static IReadOnlyList<CertifiedQuery> ForGlossary(IEnumerable<Entry> entries) =>
        [.. entries.Select(e => new CertifiedQuery(e.Name, e.Question, e.Sql, e.CertifiedAt))];

    private List<Entry> Load() =>
        File.Exists(Path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(Path)) ?? [] : [];


    private static string Normalize(string sql) =>
        Regex.Replace(sql.Trim().TrimEnd(';'), @"\s+", " ").ToLowerInvariant();
}
