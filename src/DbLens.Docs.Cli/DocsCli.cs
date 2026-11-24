using System.Text.Json;
using System.Text.Json.Serialization;
using DbLens.Docs.Documents;
using DbLens.Docs.Inventory;

namespace DbLens.Docs.Cli;

/// <summary>
/// <c>dblens-docs inventory | scaffold | check | confirm</c>. Exit code 0 is success, 1 a failed check,
/// 2 a wrong command or a folder it cannot work with. Nothing is written but the documents.
/// </summary>
public static class DocsCli
{
    private const string Usage = """
        dblens-docs <command> [root] [options]

          inventory  [root] [--out FILE]                 the inventory as JSON (stdout, or FILE)
          scaffold   [root] [--docs DIR] [--language L]  write the generated regions, keep the words
          check      [root] [--docs DIR] [--strict] [--require-slots] [--json]
                                                         fail when the documents and the code disagree
          confirm    <id> [root] [--docs DIR]            confirm a slot as it reads now (it becomes yours)
          mcp        [root]                              serve the inventory and the slots over MCP (stdio)

        root is the folder of the solution (default: the current folder). The settings are in dblens-docs.json
        ({ "language": "en" | "it", "docs": "docs/project" }); the options override them.
        """;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static int Run(string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        try
        {
            return Execute(args, output, error, currentDirectory);
        }
        catch (ArgumentException ex)
        {
            error.WriteLine($"dblens-docs: {ex.Message}");
            return 2;
        }
        catch (IOException ex)
        {
            error.WriteLine($"dblens-docs: {ex.Message}");
            return 2;
        }
    }

    private static int Execute(string[] args, TextWriter output, TextWriter error, string currentDirectory)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            (args.Length == 0 ? error : output).WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        var command = args[0];
        List<string> positional = [];
        Dictionary<string, string?> options = new(StringComparer.Ordinal);
        string[] flags = ["--strict", "--require-slots", "--json"];
        string[] valued = ["--out", "--docs", "--language"];
        for (var i = 1; i < args.Length; i++)
        {
            if (flags.Contains(args[i])) options[args[i]] = null;
            else if (valued.Contains(args[i]))
                options[args[i]] = i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            else if (args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Unknown option {args[i]}.\n\n{Usage}");
            else positional.Add(args[i]);
        }

        if (command is not ("inventory" or "scaffold" or "check" or "confirm")) throw new ArgumentException($"Unknown command {command}.\n\n{Usage}");
        if (command == "confirm" && positional.Count == 0) throw new ArgumentException("confirm needs the id of the item.");

        var rootArgument = command == "confirm" ? positional.ElementAtOrDefault(1) : positional.FirstOrDefault();
        var root = Path.GetFullPath(rootArgument ?? currentDirectory, currentDirectory);
        if (!Directory.Exists(root)) throw new ArgumentException($"{root} is not a folder.");

        var config = DocsConfig.Load(root);
        var docs = new DocsOptions(options.GetValueOrDefault("--language") ?? config.Language, options.GetValueOrDefault("--docs") ?? config.Directory);

        var read = SolutionReader.Read(root);
        var inventory = ProjectInventoryEngine.Build(read);

        switch (command)
        {
            case "inventory":
                var json = InventoryJson.Serialize(inventory);
                if (options.GetValueOrDefault("--out") is { } outFile) File.WriteAllText(Path.GetFullPath(outFile, currentDirectory), json);
                else output.Write(json);
                return 0;

            case "scaffold":
                var result = Scaffolder.Scaffold(inventory, DocsFolder.Read(root, docs), docs);
                DocsFolder.Write(root, docs, result.Files);
                var r = result.Report;
                output.WriteLine($"{result.Files.Count} documents in {docs.Folder}: {r.Added.Count} added, {r.Changed.Count} changed, {r.Removed.Count} removed, {r.Stale.Count} slots stale, {r.MarkedHuman.Count} slots marked human.");
                foreach (var id in r.Removed) output.WriteLine($"  removed: {id}");
                foreach (var id in r.Stale) output.WriteLine($"  stale slot: {id}");
                return 0;

            case "confirm":
                var id2 = positional[0];
                var documents = DocsFolder.Read(root, docs);
                var item = inventory.Items.FirstOrDefault(i => i.Id == id2) ?? throw new ArgumentException($"{id2} is not in the inventory.");
                var (path, text) = documents.FirstOrDefault(d => DocumentModel.Parse(d.Value).Blocks.Any(b => b.Id == id2 && b.Slot is not null));
                if (path is null) throw new ArgumentException($"No slot for {id2} in the documents.");
                DocsFolder.Write(root, docs, new Dictionary<string, string> { [path] = Scaffolder.Confirm(text, id2, ItemHash.Of(item)) });
                output.WriteLine($"{id2} is confirmed as human.");
                return 0;

            default: // check
                var check = DocumentCheck.Check(inventory, DocsFolder.Read(root, docs), docs, options.ContainsKey("--strict"), options.ContainsKey("--require-slots"));
                if (options.ContainsKey("--json"))
                    output.WriteLine(JsonSerializer.Serialize(new { ok = check.Ok, errors = check.Errors, warnings = check.Warnings, issues = check.Issues }, Json));
                else
                {
                    foreach (var issue in check.Issues)
                        output.WriteLine($"{(issue.Severity == IssueSeverity.Error ? "error  " : "warning")} {issue.Code,-18} {issue.File}  {issue.Message}");
                    output.WriteLine($"{check.Errors} errors, {check.Warnings} warnings.");
                }

                return check.Ok ? 0 : 1;
        }
    }
}
