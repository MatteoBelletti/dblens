using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbLens.Core.Analysis.Glossary;
using DbLens.Core.Models;
using static DbLens.Core.Analysis.Names;

namespace DbLens.Mcp.Infrastructure;

/// <summary>
/// Queries the user confirmed as correct, kept in one local JSON file and scoped to the database
/// they were certified against. Plain file, no service: certification is rare and human-paced.
/// </summary>
public sealed class CertifiedQueryStore(string path)
{
    public sealed record Entry(
        string Name, string Question, string Sql, DatabaseDialect Dialect, string Database, string CertifiedAt);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _gate = new();

    public string Path { get; } = path;

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dblens", "certified-queries.json");

    public IReadOnlyList<Entry> List(DatabaseDialect dialect, string database)
    {
        lock (_gate)
            return [.. Load().Where(e => e.Dialect == dialect && Same(e.Database, database)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public Entry? Find(DatabaseDialect dialect, string database, string name) =>
        List(dialect, database).FirstOrDefault(e => Same(e.Name, name));

    public Entry? Matching(DatabaseDialect dialect, string database, string sql) =>
        List(dialect, database).FirstOrDefault(e => Normalize(e.Sql) == Normalize(sql));

    /// <summary>Adds or replaces the entry with this name for this database.</summary>
    public Entry Save(string name, string question, string sql, DatabaseDialect dialect, string database)
    {
        var entry = new Entry(name.Trim(), question.Trim(), sql.Trim(), dialect, database,
            DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));

        lock (_gate)
        {
            var all = Load()
                .Where(e => !(e.Dialect == dialect && Same(e.Database, database) && Same(e.Name, entry.Name)))
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
