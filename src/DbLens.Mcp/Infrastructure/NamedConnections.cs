using System.Text.RegularExpressions;
using DbLens.Core.Models;
using Microsoft.Extensions.Configuration;

namespace DbLens.Mcp.Infrastructure;

/// <summary>
/// Connections saved under a name, one file each (<c>&lt;name&gt;.conn</c>: the dialect on the first
/// line, the connection string on the second) in <c>DbLens:ConnectionsPath</c>. A caller passes the
/// name, so a password written there by a local tool never travels through a prompt.
/// </summary>
public sealed partial class NamedConnections(IConfiguration configuration)
{
    public string Folder => configuration["DbLens:ConnectionsPath"] is { Length: > 0 } path
        ? path
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dblens", "connections");

    public ConfiguredDatabase Resolve(string name)
    {
        if (!NameRx().IsMatch(name))
            throw new ArgumentException($"'{name}' is not a connection name: letters, digits, '.', '-' and '_' only.");

        var file = Path.Combine(Folder, name + ".conn");
        if (!File.Exists(file))
            throw new ArgumentException($"No connection named '{name}' in {Folder}.");

        var lines = File.ReadAllLines(file);
        if (lines.Length < 2 || !Enum.TryParse<DatabaseDialect>(lines[0].Trim(), out var dialect) || string.IsNullOrWhiteSpace(lines[1]))
            throw new ArgumentException($"The connection '{name}' in {Folder} is not a dialect line followed by a connection string.");

        return new ConfiguredDatabase(dialect, lines[1].Trim(), TargetSource.Named);
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex NameRx();
}
