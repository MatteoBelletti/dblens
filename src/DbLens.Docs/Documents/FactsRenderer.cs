using System.Text;
using DbLens.Docs.Inventory;

namespace DbLens.Docs.Documents;

/// <summary>The words around the tool's tables. Only English and Italian: a language the tool does not know falls back to English.</summary>
public sealed class DocLabels
{
    private readonly Dictionary<string, string> _words;

    private DocLabels(Dictionary<string, string> words) => _words = words;

    public string this[string key] => _words.GetValueOrDefault(key, key);

    public static string Language(string? language) => language?.ToLowerInvariant() == "it" ? "it" : "en";

    public static DocLabels For(string? language) => Language(language) == "it" ? Italian : English;

    private static readonly DocLabels English = new(new()
    {
        ["verb"] = "Verb", ["route"] = "Route", ["handler"] = "Handler", ["file"] = "File", ["style"] = "Declared as",
        ["project"] = "Project", ["sdk"] = "SDK", ["output"] = "Output", ["framework"] = "Framework", ["references"] = "Project references", ["packages"] = "Packages",
        ["context"] = "DbContext", ["entities"] = "Entities", ["schema"] = "Default schema", ["efVersion"] = "EF Core", ["migrations"] = "Migrations",
        ["table"] = "Table", ["key"] = "Key", ["properties"] = "Properties", ["foreignKeys"] = "Foreign keys", ["sharesTable"] = "Shares a table with",
        ["baseAddress"] = "Base address", ["addressFrom"] = "Address read from", ["class"] = "Class", ["bases"] = "Bases", ["registered"] = "Registered with AddHostedService",
        ["touches"] = "Entities named in the handler (inferred)", ["usedBy"] = "Named by endpoints (inferred)", ["readIn"] = "read in", ["unresolved"] = "Not written as text", ["none"] = "(none)",
        ["architecture"] = "Architecture", ["architectureIntro"] = "Projects of the solution and how they reference each other. The tables are generated; the text under each is for a person.",
        ["dataModel"] = "Data model", ["dataModelIntro"] = "The DbContexts and their entities, read from the EF Core snapshots.",
        ["api"] = "API", ["apiIntro"] = "Endpoints declared in the code.", ["configuration"] = "Configuration", ["configurationIntro"] = "Configuration keys the code reads or a settings file defines. Values are never listed.",
        ["integrations"] = "Integrations", ["integrationsIntro"] = "External HTTP clients and services that run in the background.",
        ["overview"] = "Overview", ["overviewIntro"] = "How the solution is put together: its components, how they talk to each other, and the main flows. The diagrams and tables are generated from the code; the text under each is for a person.",
        ["components"] = "Components", ["flows"] = "Flows", ["flowsIntro"] = "What an entry point sets in motion, hop by hop. A deduction from names: the code is read, not run.",
        ["eventsTitle"] = "Events", ["sharedTitle"] = "Shared libraries", ["role"] = "Role", ["infrastructure"] = "Infrastructure", ["privateLibraries"] = "Private libraries",
        ["producers"] = "Produced by", ["consumers"] = "Consumed by", ["sharedUsedBy"] = "Used by", ["caller"] = "Caller", ["background"] = "in the background", ["noConsumer"] = "no consumer",
        ["involved"] = "Components involved", ["eventsWord"] = "events", ["callWord"] = "call", ["dataWord"] = "data", ["dependsWord"] = "depends on", ["event"] = "Event", ["library"] = "Library",
        ["service"] = "service", ["application"] = "application", ["endpointsTitle"] = "Endpoints", ["publishesTitle"] = "Events it publishes", ["consumesTitle"] = "Events it consumes",
        ["backgroundTitle"] = "Background services", ["dataTitle"] = "Data", ["connectionsTitle"] = "Connections", ["relatedFlows"] = "Flows it takes part in",
        ["from"] = "From", ["to"] = "To", ["kindWord"] = "Kind", ["via"] = "Via", ["flowWord"] = "Flow",
        ["narrative"] = "Narrative", ["detailIntro"] = "The detail of one component: what it exposes, publishes, consumes and takes part in, generated from the code, and below the narrative.",
    });

    private static readonly DocLabels Italian = new(new()
    {
        ["verb"] = "Verbo", ["route"] = "Route", ["handler"] = "Handler", ["file"] = "File", ["style"] = "Dichiarato come",
        ["project"] = "Progetto", ["sdk"] = "SDK", ["output"] = "Output", ["framework"] = "Framework", ["references"] = "Riferimenti a progetti", ["packages"] = "Pacchetti",
        ["context"] = "DbContext", ["entities"] = "Entità", ["schema"] = "Schema predefinito", ["efVersion"] = "EF Core", ["migrations"] = "Migration",
        ["table"] = "Tabella", ["key"] = "Chiave", ["properties"] = "Proprietà", ["foreignKeys"] = "Chiavi esterne", ["sharesTable"] = "Condivide la tabella con",
        ["baseAddress"] = "Indirizzo base", ["addressFrom"] = "Indirizzo letto da", ["class"] = "Classe", ["bases"] = "Basi", ["registered"] = "Registrato con AddHostedService",
        ["touches"] = "Entità nominate nell'handler (dedotto)", ["usedBy"] = "Nominata da endpoint (dedotto)", ["readIn"] = "letta in", ["unresolved"] = "Non scritto come testo", ["none"] = "(nessuno)",
        ["architecture"] = "Architettura", ["architectureIntro"] = "I progetti della soluzione e come si riferiscono tra loro. Le tabelle sono generate; il testo sotto ciascuna è per una persona.",
        ["dataModel"] = "Modello dati", ["dataModelIntro"] = "I DbContext e le loro entità, letti dagli snapshot di EF Core.",
        ["api"] = "API", ["apiIntro"] = "Gli endpoint dichiarati nel codice.", ["configuration"] = "Configurazione", ["configurationIntro"] = "Le chiavi di configurazione che il codice legge o che un file di impostazioni definisce. I valori non sono mai elencati.",
        ["integrations"] = "Integrazioni", ["integrationsIntro"] = "Client HTTP esterni e servizi che girano in background.",
        ["overview"] = "Panoramica", ["overviewIntro"] = "Come è fatta la soluzione: i componenti, come si parlano e i flussi principali. I diagrammi e le tabelle sono generati dal codice; il testo sotto ciascuno è per una persona.",
        ["components"] = "Componenti", ["flows"] = "Flussi", ["flowsIntro"] = "Cosa mette in moto un punto d'ingresso, passo per passo. Una deduzione dai nomi: il codice è letto, non eseguito.",
        ["eventsTitle"] = "Eventi", ["sharedTitle"] = "Librerie condivise", ["role"] = "Ruolo", ["infrastructure"] = "Infrastruttura", ["privateLibraries"] = "Librerie private",
        ["producers"] = "Prodotto da", ["consumers"] = "Consumato da", ["sharedUsedBy"] = "Usata da", ["caller"] = "Chiamante", ["background"] = "in background", ["noConsumer"] = "nessun consumatore",
        ["involved"] = "Componenti coinvolti", ["eventsWord"] = "eventi", ["callWord"] = "chiamata", ["dataWord"] = "dati", ["dependsWord"] = "dipende da", ["event"] = "Evento", ["library"] = "Libreria",
        ["service"] = "servizio", ["application"] = "applicazione", ["endpointsTitle"] = "Endpoint", ["publishesTitle"] = "Eventi che pubblica", ["consumesTitle"] = "Eventi che consuma",
        ["backgroundTitle"] = "Servizi in background", ["dataTitle"] = "Dati", ["connectionsTitle"] = "Collegamenti", ["relatedFlows"] = "Flussi a cui partecipa",
        ["from"] = "Da", ["to"] = "A", ["kindWord"] = "Tipo", ["via"] = "Tramite", ["flowWord"] = "Flusso",
        ["narrative"] = "Narrazione", ["detailIntro"] = "Il dettaglio di un componente: cosa espone, pubblica, consuma e a cosa partecipa, generato dal codice, e sotto la narrazione.",
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
                 ("sharesTable", Or(a["sharesTableWith"], l)), .. Linked("usedBy", a["usedBy"])]),
            ItemKind.Endpoint => Table(heading, item, l,
                [("verb", a["verb"]), ("route", a["route"].Length > 0 ? a["route"] : a["relativeRoute"].Length > 0 ? $"{a["relativeRoute"]} ({l["unresolved"]})" : l["unresolved"]),
                 ("handler", Or(a["handler"], l)), ("style", a["style"]), .. Linked("touches", a["touches"]), ("file", item.Source.File)]),
            ItemKind.HttpClient => Table(heading, item, l,
                [("project", a["project"]), ("baseAddress", Or(a["baseAddress"], l)), ("addressFrom", a["baseAddressFrom"]), ("file", item.Source.File)]),
            ItemKind.HostedService => Table(heading, item, l,
                [("bases", Or(a["bases"], l)), ("registered", a["registeredWithAddHostedService"]), ("file", item.Source.File)]),
            ItemKind.Config => $"- `{item.Name}` — {l["readIn"]} {a["readIn"]} ({item.Source.File})",
            _ => heading,
        };
    }

    // A link column only where there is a link: a solution without a data model has no empty column.
    private static IEnumerable<(string, string)> Linked(string label, string value) => value.Length == 0 ? [] : [(label, value)];

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
