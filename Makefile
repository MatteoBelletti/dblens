.PHONY: dev dev-server inspector-sse attach test test-watch build publish db db-down db-logs

MCP_PROJECT  := src/DbLens.Mcp
PUBLISH_DIR  := publish
MCP_SSE_URL  := http://localhost:5050/mcp

# ── Sviluppo (stdio) ──────────────────────────────────────────────────────────

# Inspector + auto-rebuild su ogni salvataggio (trasporto stdio, no debugger)
dev:
	npx --yes @modelcontextprotocol/inspector dotnet watch run --project $(MCP_PROJECT) --non-interactive

# ── Sviluppo con debugger (SSE) ───────────────────────────────────────────────
#
# Workflow:
#   Terminal 1:  make dev-server     (avvia il server su http://localhost:5050)
#   VS Code:     F5 → "Attach"       (attacca il debugger al processo in corso)
#   Terminal 2:  make inspector-sse  (apre l'inspector collegato al server SSE)

# Avvia il server con trasporto SSE + auto-rebuild (pronto per attach debugger)
dev-server:
	MCP_TRANSPORT=sse dotnet watch run --project $(MCP_PROJECT) --non-interactive

# Apre l'MCP Inspector — nel browser seleziona "Streamable HTTP" e incolla l'URL
inspector-sse:
	@echo "Inspector avviato. Nel browser:"
	@echo "  1. Seleziona 'Streamable HTTP' come transport"
	@echo "  2. Incolla: $(MCP_SSE_URL)"
	npx --yes @modelcontextprotocol/inspector

# Attacca il debugger di Visual Studio al processo DbLens.Mcp in esecuzione.
# Se ci sono più istanze mostra un menu interattivo.
attach:
	powershell -ExecutionPolicy Bypass -File scripts/attach-debugger.ps1

# ── Test ──────────────────────────────────────────────────────────────────────

test:
	dotnet test --logger "console;verbosity=normal"

test-watch:
	dotnet watch test --project tests/DbLens.Tests

# ── Build & publish ───────────────────────────────────────────────────────────

build:
	dotnet build

publish:
	dotnet publish $(MCP_PROJECT) -c Release -o $(PUBLISH_DIR)
	@echo ""
	@echo "Eseguibile: $(PUBLISH_DIR)/DbLens.Mcp.exe"
	@echo ""
	@echo "Aggiorna claude_desktop_config.json con:"
	@echo '  "command": "$(CURDIR)/$(PUBLISH_DIR)/DbLens.Mcp.exe"'

# ── Database ──────────────────────────────────────────────────────────────────

db:
	docker compose up -d
	@echo "Attendo che i database siano pronti..."
	docker compose ps

db-down:
	docker compose down

db-logs:
	docker compose logs -f
