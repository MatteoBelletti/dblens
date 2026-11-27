# DbLens.Docs — N4 e N5: citazioni controllate e prova finale

Piano: [Piano 2](https://claude.ai/artifact/CY9So17wnufFQ4nWzfXFgF). Prima: [docs-n2.md](docs-n2.md).

## N4: cosa c'è

Una frase che cita il codice cita un luogo che il codice può confermare. Nelle pagine narrative (`detail:`) una citazione è
`File.cs: Metodo` o `Tipo: Membro` tra apici inversi (metodo, costruttore, proprietà, campo o membro di un enum; anche ereditato da un tipo dei sorgenti). Un tipo che i sorgenti non dichiarano, o un membro che potrebbe venire da una classe base non visibile, non si controlla:

- `set_doc_slot` rifiuta parole che citano ciò che non c'è, nominando le citazioni, e una pagina `detail:` senza la sezione «Non verificato»: un modello non può inventare una citazione;
- conserva nel marcatore dello slot l'hash del codice citato (`refs=`), senza spazi né altri metodi dentro;
- `check` segnala `slot-bad-reference` (la citazione non regge più), `slot-stale-reference` (il codice citato è cambiato dopo
  la scrittura) e `slot-no-unverified-section` (la pagina non dice cosa non è stato letto). Avvisi; errori con `--strict`;
- `get_doc_slots` elenca come `stale` lo slot il cui codice citato è cambiato, così `fill-slots` lo riscrive e basta quello.

Non costruito, per scelta: `get_component_facts` (i fatti e i puntatori in ordine di flusso sono già nel documento di dettaglio,
che lo scrittore legge prima) e l'etichetta «letto dal codice / interpretazione» come attributo controllato (resta una regola
di scrittura).

## N5: prova finale su eShop

Panoramica (0,54 $) e dettaglio di `Ordering.API` (0,29 $) e di `WebApp` (0,28 $), in italiano; tutte le citazioni risolte dal check.
Le quattro insidie del documento scritto a mano:

| Insidia | Esito |
|---|---|
| Stati e tempi, periodo di grazia | Trovata nella panoramica (il servizio in background che cerca gli ordini `Submitted` oltre `GracePeriodTime`); il «circa un minuto e mezzo» non c'è |
| Eventi falliti non ripubblicati | Trovata nel dettaglio di `Ordering.API`, con la cautela giusta («non ho trovato un nuovo tentativo in questi file») |
| Pagamento simulato da un'impostazione | In una prova sì («simula il pagamento… non contatta alcun gateway»), in un'altra solo «legge le PaymentOptions»: dipende dalla prova |
| La WebApp ignora la risposta di Ordering.API e svuota il carrello | **Non trovata**, né nel dettaglio di `WebApp` |

La quarta sta in `Checkout.razor`: il tool legge C# e non i file `.razor`, quindi non compare tra i fatti né tra i puntatori, e
una citazione `Checkout.razor: …` non è riconosciuta. È il limite principale che resta.
