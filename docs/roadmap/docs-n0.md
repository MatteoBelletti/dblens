# DbLens.Docs — N0: spike su macrocomponenti, eventi, chiamate e flussi

Piano: [Piano 2](https://claude.ai/artifact/CY9So17wnufFQ4nWzfXFgF). Domanda dello spike: quanto del raggruppamento in
macrocomponenti e dei collegamenti tra loro si ricava dalla sola sintassi? Codice in `spikes/docs-n0` (usa i file letti e
l'inventario di `DbLens.Docs`, analizza con Roslyn, non compila né esegue niente; non è in `dblens.slnx` e lo rimpiazza N1).

## Come è stato misurato

Una **verità scritta a mano** per eShop (commit `e5e24f0`), fatta con `grep` e leggendo il codice prima di far girare il
prototipo (`spikes/docs-n0/groundtruth-eshop.txt`), e una per EventualShop (`groundtruth-eventualshop.txt`), ricavata con un
passaggio di espressioni regolari indipendente da Roslyn. Una terza soluzione, eShopOnWeb, è stata solo guardata.

| Soluzione | Stile | File C# | Tempo |
|---|---|---|---|
| `dotnet/eShop` | microservizi, event bus proprio su RabbitMQ, Aspire | 471 | 1,6 s |
| `AntonioFalcaoJr/EventualShop` (MIT) | microservizi CQRS, event sourcing, MassTransit | 960 | 2,6 s |
| `dotnet-architecture/eShopOnWeb` (MIT) | monolite a livelli | 199 | 1,2 s |

## Risultati

| Cosa | eShop | EventualShop |
|---|---|---|
| Macrocomponenti (eseguibili non di test) | 11 su 11 | 18, struttura giusta, nomi da rivedere |
| Librerie private e condivise | 2/2 e 5/5 | corrette a vista (una sola condivisa, `Contracts`) |
| Dove nascono gli eventi (produttori) | 13/13, 0 in più | 71/71, 0 in più |
| Chi li consuma (sottoscrizioni) | 18/18, 0 in più | 57/57, 0 in più |
| Archi tra macro per evento | 18/18 | 60/60 |
| Catene "evento consumato → eventi che ne derivono" | 8/8, 0 in più | **0: non misurabile** |
| Entry point (endpoint con gestore nominabile, servizio in background) → eventi | 7/7 | **0** |
| Chiamate sincrone (HTTP, gRPC, forwarder) verso un servizio | 5/5 | **0: non si possono abbinare** |
| Dati letti da un'altra macro (SQL a mano) | 1/1 | 0 trovati |

La catena che avevo segnalato come insidia nel documento scritto a mano ("scorta rifiutata → nessun evento di annullamento")
viene riprodotta: il prototipo non la collega.

## Cosa ho imparato

1. **Gli eventi sono la parte più solida.** Con un *preset* per ciascuno stile si ricavano produttori e consumatori in modo
   esatto su entrambe le soluzioni. Il preset deve nominare: i tipi che marcano un evento (`IntegrationEvent`, `IEvent`,
   `IDomainEvent`), come si registra un consumatore (`AddSubscription<E, H>`, `IConsumer<E>`, `Consumer<E>`) e **quali
   metodi producono** (`new E`, ma anche `RaiseEvent<E>`, `Publish<E>`, `Send<E>`): con "qualsiasi chiamata generica" i
   produttori erano 121 invece di 71, perché contava anche le registrazioni nell'iniezione delle dipendenze.
2. **Le catene vanno fermate al bus.** Un evento d'integrazione non si segue fino ai gestori degli altri servizi, che girano
   altrove e dopo; si seguono solo comandi e eventi di dominio (mediator). Prima di questa regola le catene erano 26, con 18
   false. La regola l'ho trovata guardando il risultato su eShop, la stessa soluzione su cui l'ho misurata: sulla seconda
   soluzione non l'ho potuta verificare, perché le catene non vengono fuori (vedi 4).
3. **Il nome del progetto non è il nome della macro.** In EventualShop 16 eseguibili si chiamano `GrpcService` o
   `WorkerService`. Con il percorso (`Account/Query`, `Account/Command`) diventano leggibili. Resta da decidere se `Command`
   e `Query` dello stesso servizio sono una macro o due: oggi due.
4. **Dove la logica passa per un'interfaccia o per una classe base generica, la sintassi non arriva.** In EventualShop il
   consumatore eredita da `Consumer<T>`, che chiama `_interactor.InteractAsync(...)` su un'interfaccia: il nome del metodo
   ha molte dichiarazioni e non si risolve senza leggere le registrazioni. In eShop il prototipo ha saltato 335 chiamate
   perché il nome non era unico. Le catene funzionano dove i gestori sono classi concrete con nomi di metodo non ambigui.
5. **I riferimenti tra progetti per nome sono ambigui.** Con un `Application` e un `Domain` per servizio, l'attributo
   `projectReferences` dell'inventario (solo i nomi) non dice a quale progetto punta. Per N1 l'inventario deve registrare
   il progetto di destinazione risolto per percorso.
6. **L'AppHost di Aspire non ha una forma sola.** In eShop non usa `AddProject<Projects.X>` ma `AddDotnetProject("nome",
   "../Percorso")`: bisogna riconoscere entrambe. Senza AppHost (EventualShop) i nomi host delle chiamate non si abbinano a
   nessun servizio: servirebbero `docker-compose.yml` o la configurazione, file che `SolutionReader` oggi non apre.
7. **Un difetto vero in `DbLens.Docs`, trovato dalla seconda soluzione e corretto.** L'inventario andava in crash quando lo
   stesso nome di `DbContext` compare in più progetti (EventualShop ne ha otto, uno per servizio): gli id collidevano e a
   tutti veniva assegnato il file del primo snapshot. Ora `SolutionRead` porta il file di ogni snapshot, e l'id di un contesto
   ripetuto contiene la cartella (`context:EventStoreDbContext [src/Services/Order/Command/Infrastructure.EventStore]`).
   Quattro test nuovi (`RepeatedContextTests`).
8. **Un monolite ha una forma diversa.** eShopOnWeb dà 3 macro (`Web`, `PublicApi`, `BlazorAdmin`) e 3 librerie condivise
   (`ApplicationCore`, `Infrastructure`, `BlazorShared`). Chiamare "Condiviso" tutto ciò che usano in due nasconde proprio il
   nucleo del dominio: in un monolite le librerie condivise vanno elencate una per una, con chi le usa.

## Limiti di questa misura

- Una sola verità scritta a mano per le catene, e su una sola soluzione.
- La verità su EventualShop segue le stesse definizioni del prototipo: misura che l'implementazione le applichi bene, non che
  le definizioni catturino tutto. Un evento pubblicato in modo dinamico (`Publish(@event, @event.GetType())`) non viene
  attribuito a chi lo pubblica: lo si attribuisce a chi lo crea.
- Gli eventi si abbinano per nome (`Contenitore.Nome`): due eventi con lo stesso nome in servizi diversi si confonderebbero.
  In EventualShop 70 tipi corrispondono a 68 chiavi, quindi due nomi si ripetono.
- Gli endpoint con una lambda inline (10 in eShop, 61 in EventualShop) non sono entry point nel prototipo. L'estrattore di
  N1 ha già il corpo della lambda, quindi è un limite dello spike e non della soluzione.
- Il codice ricavato è su eShop `e5e24f0`, non sul commit della fixture di test (`669682e`, ridotta): N1 deve ampliare la
  fixture con i file che servono (gestori, dominio, `AppHost.cs`), restando nei limiti della licenza MIT.

## Decisione per N1

**Si va avanti**, con questo perimetro, ordinato per quanto lo spike lo sostiene:

1. Eventi con preset (eShop bus, MassTransit-style con `IConsumer`/`Consumer`, schemi propri in configurazione), produttori
   per `new` o per i metodi produttori nominati, archi tra macro, e catene che si fermano al bus.
2. Macrocomponenti: eseguibili con librerie private, nomi dal percorso quando si ripetono, librerie condivise elencate una
   per una con chi le usa, `Command`/`Query` come due macro con possibilità di unirle in `dblens-docs.json`.
3. Inventario v2 con riferimenti tra progetti risolti per percorso.
4. Chiamate sincrone da AppHost (entrambe le forme) e da URL con nome di servizio; il resto in `Coverage`.
5. Dati letti a mano con SQL, come nello spike.
6. Da misurare in N1 e non da promettere: catene attraverso interfacce e classi base generiche.

**Una domanda per te, perché cambia `docs/safety.md`:** vuoi che il tool legga anche `docker-compose.yml` e simili (nomi dei
servizi e dei loro collegamenti)? Senza, le chiamate sincrone si ricavano solo con un AppHost di Aspire.
