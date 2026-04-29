# Guida allo sviluppo — DbLens MCP Server

## Prerequisiti

- .NET 10 SDK
- Docker Desktop
- Node.js / npx (per l'MCP Inspector)
- Visual Studio 2026 con `dblens.slnx` aperta
- `make` — installa con `winget install GnuWin32.Make` poi riapri il terminale

---

## Setup iniziale

```powershell
# Avvia i database PostgreSQL e SQL Server
make db
```

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

## Struttura del progetto

```
src/
  DbLens.Core/        → interfacce e modelli condivisi
  DbLens.Postgres/    → implementazione PostgreSQL
  DbLens.SqlServer/   → implementazione SQL Server
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

## Configurazione Claude Desktop

File: `%APPDATA%\Claude\claude_desktop_config.json`

```json
{
  "mcpServers": {
    "dblens": {
      "command": "C:\\Users\\matte_cc6iqkl\\dblens\\publish\\DbLens.Mcp.exe",
      "args": []
    }
  }
}
```

> Dopo ogni `make publish` riavvia Claude Desktop per caricare il nuovo exe.
