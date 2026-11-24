# DbLens.Docs — N1: estrattori e inventario v2

Piano: [Piano 2](https://claude.ai/artifact/CY9So17wnufFQ4nWzfXFgF). Spike che l'ha preceduta: [docs-n0.md](docs-n0.md).
Uscita richiesta: JSON identico a ogni run, su eShop e su una seconda soluzione.

## Cosa c'è

- **Schema 2 dell'inventario**, con quattro nuovi tipi di elemento: `component:`, `event:`, `link:`, `flow:` (guida in
  [../dblens-docs.md](../dblens-docs.md)), e l'evidenza `Inferred` per ciò che è una deduzione sui nomi.
- `ComponentModel`: eseguibili con le librerie private, librerie condivise elencate con chi le usa, nomi dal percorso
  quando si ripetono (almeno due cartelle: `Cart/Command`), unione in `dblens-docs.json`; l'orchestratore e i progetti di
  test non sono componenti.
- `EventAnalysis`: eventi, produttori (`new E` o un metodo produttore con `E` come argomento di tipo), consumatori
  (`AddSubscription`, `IConsumer`, `Consumer`, `IIntegrationEventHandler`) e raggiungibilità, con la regola che ferma
  l'evento al bus.
- `AppHostReader` (le due forme: `AddProject<Projects.X>` e `AddDotnetProject("nome", "../Percorso")`), `ComposeReader`
  (YAML; servizi, build, `depends_on`, ambiente solo come nomi di servizio) e collegamenti per URL e per SQL a mano.
- `projectReferenceIds` sui progetti: i riferimenti risolti per percorso, perché i nomi si ripetono.
- `SolutionRead.ModelSources`: il file di ogni snapshot, così un `DbContext` ripetuto in più servizi non va in crash né
  punta al file sbagliato (id con la cartella).
- I file compose entrano tra quelli che `SolutionReader` apre (sezione in [../safety.md](../safety.md)).
- `InventoryOptions` e `DocsConfig.LoadInventory`: preset e configurazione.

## Misure

Contro le verità scritte a mano (eShop, tutti i sorgenti) e trovate con una regex indipendente (EventualShop, sottoinsieme),
e con i repo completi per vedere che non vadano in crash:

| | eShop | EventualShop (sottoinsieme) |
|---|---|---|
| Componenti, librerie private e condivise | 11, 2, 5: tutte | 4 più `Contracts`: tutte |
| Produttori, consumatori, archi tra componenti | 13, 18, 18: tutti, 0 in più | 22, 18, 18: tutti, 0 in più |
| Chiamate sincrone e dati letti a mano | 5 e 1: tutte | (nessuna senza AppHost) |
| Infrastruttura dei servizi dal compose | — | `mssql`, `mongodb`, `rabbitmq` per servizio |
| Flusso del periodo di grazia | i 13 passi del documento scritto a mano | — |

Sui repo completi: eShop (1,5 s), eShopOnWeb (1,3 s) e EventualShop (2,4 s, 8 contesti con lo stesso nome) girano senza id
duplicati. Il JSON è identico a ogni run, anche con i file in un altro ordine; i riferimenti sono tre
(`eShopFlows`, `EventualShop`, e quelli di M1/M2 rigenerati).

## Decisioni e scostamenti

- **I nuovi tipi non sono ancora nei cinque documenti.** `DocPlan` li esclude: il documento d'ingresso è N2.
- **Un flusso parte solo da un endpoint o da un servizio in background.** Il corpo di una lambda inline è ora un punto di
  partenza (nello spike non lo era).
- **L'evidenza dei flussi e dei collegamenti per URL o SQL è `Inferred`**; quella degli eventi, dei riferimenti AppHost e
  delle dipendenze compose è `Extracted`.
- **Un evento solo consumato o solo prodotto è `Unresolved`** con la ragione, e finisce in `Coverage`.
- **Nomi dei componenti**: i tre problemi visti nello spike sono risolti (nome dal percorso, almeno due cartelle, unione in configurazione).
- **Libreria nuova**: `YamlDotNet` 16.3.0 (MIT), usata solo per leggere i file compose.

## Difetti trovati e corretti per strada

1. Due `.csproj` nella stessa cartella mandavano in crash `AppHostReader` (dizionario per cartella).
2. Un compose che divide servizi e infrastruttura su due file perdeva le dipendenze (i nomi si filtravano per file).
3. Il lettore dell'AppHost associava `postgres.AddDatabase("db")` a `postgres` invece che a `db`.

## Limiti che restano

- Una catena si ferma a un'interfaccia, a una classe base generica e a un nome di metodo che più metodi portano: in
  EventualShop, dove i consumatori chiamano `IInteractor.InteractAsync`, non si formano catene (non è nel sottoinsieme
  misurato: i flussi lì sono 0).
- Gli eventi si abbinano per nome (`Contenitore.Nome`): due eventi con lo stesso nome in servizi diversi si confondono.
- Le chiamate sincrone si abbinano solo se il nome di host è quello di una risorsa dell'AppHost o di un servizio compose.
- Una verità scritta a mano c'è per le catene di una sola soluzione.
