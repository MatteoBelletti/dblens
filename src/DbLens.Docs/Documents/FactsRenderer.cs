using System.Text;
using DbLens.Docs.Inventory;

namespace DbLens.Docs.Documents;

/// <summary>The words around the tool's tables. Only English and Italian: a language the tool does not know falls back to English.</summary>
public sealed class DocLabels
{
    private readonly Dictionary<string, string> _words;

    private DocLabels(Dictionary<string, string> words) => _words = words;

    public string this[string key] => _words.GetValueOrDefault(key, key);

    public static DocLabels For(string? language) => language?.ToLowerInvariant() == "it" ? Italian : English;

    private static readonly DocLabels English = new(new()
    {
        ["verb"] = "Verb", ["route"] = "Route", ["handler"] = "Handler", ["file"] = "File", ["style"] = "Declared as",
        ["project"] = "Project", ["sdk"] = "SDK", ["output"] = "Output", ["framework"] = "Framework", ["references"] = "Project references", ["packages"] = "Packages",
        ["context"] = "DbContext", ["entities"] = "Entities", ["schema"] = "Default schema", ["efVersion"] = "EF Core", ["migrations"] = "Migrations",
        ["entity"] = "Entity", ["table"] = "Table", ["key"] = "Key", ["properties"] = "Properties", ["foreignKeys"] = "Foreign keys", ["sharesTable"] = "Shares a table with",
        ["client"] = "Client", ["baseAddress"] = "Base address", ["addressFrom"] = "Address read from", ["class"] = "Class", ["bases"] = "Bases", ["registered"] = "Registered with AddHostedService",
        ["readIn"] = "read in", ["unresolved"] = "Not written as text", ["none"] = "(none)",
        ["architecture"] = "Architecture", ["architectureIntro"] = "Projects of the solution and how they reference each other. The tables are generated; the text under each is for a person.",
        ["dataModel"] = "Data model", ["dataModelIntro"] = "The DbContexts and their entities, read from the EF Core snapshots.",
        ["api"] = "API", ["apiIntro"] = "Endpoints declared in the code.", ["configuration"] = "Configuration", ["configurationIntro"] = "Configuration keys the code reads or a settings file defines. Values are never listed.",
        ["integrations"] = "Integrations", ["integrationsIntro"] = "External HTTP clients and services that run in the background.",
    });

    private static readonly DocLabels Italian = new(new()
    {
        ["verb"] = "Verbo", ["route"] = "Route", ["handler"] = "Handler", ["file"] = "File", ["style"] = "Dichiarato come",
        ["project"] = "Progetto", ["sdk"] = "SDK", ["output"] = "Output", ["framework"] = "Framework", ["references"] = "Riferimenti a progetti", ["packages"] = "Pacchetti",
        ["context"] = "DbContext", ["entities"] = "Entità", ["schema"] = "Schema predefinito", ["efVersion"] = "EF Core", ["migrations"] = "Migration",
        ["entity"] = "Entità", ["table"] = "Tabella", ["key"] = "Chiave", ["properties"] = "Proprietà", ["foreignKeys"] = "Chiavi esterne", ["sharesTable"] = "Condivide la tabella con",
        ["client"] = "Client", ["baseAddress"] = "Indirizzo base", ["addressFrom"] = "Indirizzo letto da", ["class"] = "Classe", ["bases"] = "Basi", ["registered"] = "Registrato con AddHostedService",
        ["readIn"] = "letta in", ["unresolved"] = "Non scritto come testo", ["none"] = "(nessuno)",
        ["architecture"] = "Architettura", ["architectureIntro"] = "I progetti della soluzione e come si riferiscono tra loro. Le tabelle sono generate; il testo sotto ciascuna è per una persona.",
        ["dataModel"] = "Modello dati", ["dataModelIntro"] = "I DbContext e le loro entità, letti dagli snapshot di EF Core.",
        ["api"] = "API", ["apiIntro"] = "Gli endpoint dichiarati nel codice.", ["configuration"] = "Configurazione", ["configurationIntro"] = "Le chiavi di configurazione che il codice legge o che un file di impostazioni definisce. I valori non sono mai elencati.",
        ["integrations"] = "Integrazioni", ["integrationsIntro"] = "Client HTTP esterni e servizi che girano in background.",
    });
}

/// <summary>The generated text of an item: a heading and a table of what the code states. No model writes any of it.</summary>
internal static class FactsRenderer
{
    public static string Render(InventoryItem item, DocLabels l)
    {
        var a = item.Attributes;
        var heading = $"### {Cell(item.Name)}";

        return item.Kind switch
        {
            ItemKind.Project => Table(heading, item, l,
                [("sdk", a["sdk"]), ("output", $"{a["outputType"]} ({a["outputTypeFrom"]})"), ("framework", a["targetFrameworks"]),
                 ("references", Or(a["projectReferences"], l)), ("packages", Or(a["packages"], l)), ("file", item.Source.File)]),
            ItemKind.Context => Table(heading, item, l,
                [("schema", Or(a["defaultSchema"], l)), ("efVersion", Or(a["efVersion"], l)), ("entities", a["entities"]), ("migrations", a["migrations"]), ("file", item.Source.File)]),
            ItemKind.Entity => Table(heading, item, l,
                [("context", a["context"]), ("table", a["table"]), ("key", Or(a["key"], l)), ("properties", a["properties"]), ("foreignKeys", a["foreignKeys"]),
                 ("sharesTable", Or(a["sharesTableWith"], l))]),
            ItemKind.Endpoint => Table(heading, item, l,
                [("verb", a["verb"]), ("route", a["route"].Length > 0 ? a["route"] : a["relativeRoute"].Length > 0 ? $"{a["relativeRoute"]} ({l["unresolved"]})" : l["unresolved"]),
                 ("handler", Or(a["handler"], l)), ("style", a["style"]), ("file", item.Source.File)]),
            ItemKind.HttpClient => Table(heading, item, l,
                [("project", a["project"]), ("baseAddress", Or(a["baseAddress"], l)), ("addressFrom", a["baseAddressFrom"]), ("file", item.Source.File)]),
            ItemKind.HostedService => Table(heading, item, l,
                [("bases", Or(a["bases"], l)), ("registered", a["registeredWithAddHostedService"]), ("file", item.Source.File)]),
            ItemKind.Config => $"- `{item.Name}` — {l["readIn"]} {a["readIn"]} ({item.Source.File})",
            _ => heading,
        };
    }

    private static string Table(string heading, InventoryItem item, DocLabels l, (string Label, string Value)[] rows)
    {
        StringBuilder text = new();
        text.Append(heading).Append("\n\n");
        text.Append("| ").Append(string.Join(" | ", rows.Select(r => l[r.Label]))).Append(" |\n");
        text.Append("| ").Append(string.Join(" | ", rows.Select(_ => "---"))).Append(" |\n");
        text.Append("| ").Append(string.Join(" | ", rows.Select(r => Cell(r.Value)))).Append(" |");
        if (item.Evidence == ItemEvidence.Unresolved && item.Reason is not null)
            text.Append("\n\n> ").Append(Cell(item.Reason));
        return text.ToString();
    }

    private static string Or(string value, DocLabels l) => value.Length == 0 ? l["none"] : value;

    // A pipe or a line break would end the cell or the row.
    private static string Cell(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
