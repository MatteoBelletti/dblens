# Guida allo sviluppo — DbLens MCP Server

## Prerequisiti

- .NET 10 SDK
- Docker (Docker Desktop su Windows e macOS)
- Node.js / npx (per l'MCP Inspector)
- Un IDE: Visual Studio 2026, Rider o VS Code con C# Dev Kit, su `dblens.slnx`
- `make` — già presente su Linux e macOS; su Windows `winget install GnuWin32.Make`, poi riapri il terminale

`make attach` e il profilo di debug di Visual Studio esistono solo su Windows; il resto funziona
ovunque.

---

## Setup iniziale

```bash
make db          # PostgreSQL su 5433, SQL Server su 1434 (il primo avvio richiede ~1 minuto)
dotnet build
dotnet test      # i test di integrazione usano entrambi i database
```

Se un database non è raggiungibile i test di integrazione vengono **saltati**, non superati, e la CI
fallisce su qualsiasi test saltato.

---

## Workflow A — Sviluppo veloce (senza debugger)

Inspector + auto-rebuild in un solo comando. Ogni volta che salvi un file il server si ribuilda e nell'inspector clicchi **Reconnect**.

```powershell
make dev
```

Si apre il browser su `http://localhost:6274`. Nessun riavvio di Claude Desktop necessario.

---

## Workflow B — Debug con Visual Studio

### 1. Avvia il server in modalità SSE

**Da Visual Studio** *(server + debugger già attaccato)*
Nel dropdown accanto a ▶ seleziona il profilo **`sse`** → premi **F5**.
Il server parte su `http://localhost:5050`.

**Da terminale** *(poi attach manuale)*
```powershell
make dev-server
```

### 2. Attacca il debugger (solo se hai usato il terminale)

```powershell
make attach
```

Lo script trova i processi `DbLens.Mcp` in esecuzione, mostra un menu se ce ne sono più di uno, e si attacca a Visual Studio via COM automation senza aprire nuove finestre.

### 3. Avvia l'MCP Inspector

```powershell
make inspector-sse
```

Nel browser (`http://localhost:6274`):
- Transport → **Streamable HTTP**
- URL → `http://localhost:5050/mcp`
- Clicca **Connect**

### 4. Triggera i breakpoint

Chiama un tool dall'inspector → Visual Studio si ferma sul breakpoint.

### Dopo una modifica al codice

`dotnet watch` ribuilda automaticamente. Il debugger si stacca — rilancia `make attach` per riagganciarlo al nuovo processo.

---

## Reload per Claude Desktop / Claude Code

I client MCP tengono il server in vita per tutta la sessione. Per caricare le modifiche:

### 1. Pubblica il nuovo eseguibile

```powershell
make publish
```

Oppure da Visual Studio: tasto destro su **DbLens.Mcp → Pubblica → Profilo `local` → Pubblica**.

### 2. Riavvia il client

| Client | Come ricaricare |
|---|---|
| **Claude Desktop** | Chiudi e riapri l'app |
| **Claude Code** | `/mcp` → dblens → **Restart**, oppure riapri la sessione |

---

## Riferimento comandi

| Comando | Descrizione |
|---|---|
| `make dev` | Inspector + server stdio con auto-rebuild |
| `make dev-server` | Server SSE su `http://localhost:5050` con auto-rebuild |
| `make inspector-sse` | Apre l'MCP Inspector (da usare con `dev-server`) |
| `make attach` | Attacca il debugger di Visual Studio al processo in esecuzione |
| `make build` | Build della solution |
| `make test` | Esegui i test |
| `make test-watch` | Test in watch mode |
| `make publish` | Pubblica l'exe in `publish/` |
| `make db` | Avvia i database Docker |
| `make db-down` | Ferma i database Docker |
| `make db-logs` | Tail dei log dei container |

---

## Stile del codice

Le regole C# sono in `.editorconfig` e la build le applica: con `EnforceCodeStyleInBuild` e
`TreatWarningsAsErrors` una regola a `warning` è un errore di compilazione. Per applicare le
correzioni automatiche:

```bash
dotnet format style dblens.slnx
```

Attenzione ai predicati di FluentAssertions (`Should().Contain(x => …)`): sono expression tree, che non
accettano `is null` / `is not null`; lì resta `== null` / `!= null`.

---

## Struttura del progetto

Dettagli in [docs/architecture.md](docs/architecture.md).

```
src/
  DbLens.Core/        → interfacce, modelli, engine puri, DatabaseInspectorBase
  DbLens.Postgres/    → SQL di catalogo e hook PostgreSQL
  DbLens.SqlServer/   → SQL di catalogo e hook SQL Server
  DbLens.Mcp/         → server MCP — entrypoint, tool, DI
    Properties/
      launchSettings.json     → profili VS (stdio / sse)
      PublishProfiles/
        local.pubxml          → profilo publish verso publish/
tests/
  DbLens.Tests/       → test xUnit
publish/              → exe pubblicato (puntato da Claude Desktop)
scripts/
  attach-debugger.ps1 → attach automatico del debugger VS
```

---

## Trasporti

| `MCP_TRANSPORT` | Porta | Quando usarlo |
|---|---|---|
| `stdio` (default) | — | Claude Desktop, Claude Code, `make dev` |
| `sse` | 5050 | Debug con Visual Studio + inspector |

Il profilo VS **`sse`** imposta automaticamente `MCP_TRANSPORT=sse`.

---

## Login di sola lettura

dblens si collega come `dblens_reader` (creato da `db/*/03-reader.sql`). Su un server remoto rifiuta di
eseguire SQL del chiamante con un login che può scrivere; su un database locale (localhost, loopback,
socket, `(local)`, LocalDB) il controllo è saltato, quindi `postgres` / `sa` funzionano. Se dblens gira
su un vero server di database: `DbLens__ReadOnlyLoginOnLocalhost=true`. Per un database remoto
usa-e-getta: `DbLens__AllowWriteCapableLogin=true`. Per abilitare
`query_with_receipt`: `DbLens__AllowQueryResults=true`.

---

## Release

Aggiorna `<Version>` in `src/DbLens.Mcp/DbLens.Mcp.csproj` e le due versioni in
`src/DbLens.Mcp/.mcp/server.json`, fai il merge su `master`, poi:

```bash
git tag v<versione>
git push origin v<versione>
```

La versione pubblicata è quella del tag. Il workflow `release.yml` produce binari self-contained,
estensioni `.mcpb` e il pacchetto NuGet `DbLens.Mcp`, e lo pubblica su nuget.org con Trusted
Publishing (token OIDC di GitHub, nessuna API key salvata). La configurazione una tantum — policy su
nuget.org e secret GitHub `NUGET_USER` con il nome profilo nuget.org — è già fatta; senza il secret il
push viene saltato e il `.nupkg` resta allegato alla release.

La pagina NuGet mostra il README contenuto nel pacchetto: i link devono essere assoluti, e una modifica
al README arriva su nuget.org solo con la release successiva.

---

## Configurazione Claude Desktop

File: `%APPDATA%\Claude\claude_desktop_config.json` su Windows,
`~/Library/Application Support/Claude/claude_desktop_config.json` su macOS.

```json
{
  "mcpServers": {
    "dblens": {
      "command": "C:\\path\\to\\dblens\\publish\\DbLens.Mcp.exe",
      "args": []
    }
  }
}
```

> Dopo ogni `make publish` riavvia Claude Desktop per caricare il nuovo exe.
