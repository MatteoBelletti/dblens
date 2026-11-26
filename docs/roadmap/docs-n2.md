# DbLens.Docs — N2/N3: panoramica e dettaglio di un componente

Piano: [Piano 2](https://claude.ai/artifact/CY9So17wnufFQ4nWzfXFgF). Prima: [docs-n1.md](docs-n1.md).

## Cosa c'è

- `overview.md`: grafo dei componenti (Mermaid), tabella e didascalia (2–4 righe) per componente, un diagramma di
  sequenza e una didascalia per flusso, tabella degli eventi e delle librerie condivise. Tutto tranne le didascalie è
  generato dall'inventario.
- `components/<slug>.md` (`dblens-docs detail <nome>`): i fatti del componente (endpoint, eventi pubblicati e
  consumati, dati, collegamenti, flussi) e uno slot narrativo fino a 80 righe, scritto dopo aver letto il codice.
- `get_doc_slots` restituisce la `language` di `dblens-docs.json`; writer e `slot-rules` scrivono in quella lingua.
- Skill `detail-component`: chiede quale componente, avvia il documento (se non c'è già) e riempie lo slot.

## Simulazione su eShop (in italiano)

| Caso | Esito | Costo |
|---|---|---|
| `sim-overview`: 17 slot (10 componenti, 6 flussi, ClientApp/HybridApp) | punteggio 1,00, frasi fedeli al codice | 0,54 $ |
| `sim-detail`: dettaglio di `Ordering.API` | punteggio 1,00; percorso di un comando, eventi, errori, punti da toccare, «Non verificato» | 0,27 $ |

La prima prova della panoramica uscì in inglese su un documento italiano: da lì `language` in `get_doc_slots`.
La prima prova del dettaglio fallì perché il Bash dell'agente non vede `dblens-docs`: la skill ora guarda prima se lo slot
esiste, e la simulazione avvia il documento nello scaffold. Una sola prova in più oltre le tre della fase, per la lingua.

## Limiti

- Le catene non attraversano interfacce e classi base (N1): un flusso può fermarsi prima del vero consumatore.
- Nessun controllo automatico dei riferimenti `file: metodo` nello slot narrativo (N4, non costruito): li verifica il reviewer.
