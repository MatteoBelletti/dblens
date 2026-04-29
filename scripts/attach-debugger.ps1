# Attacca il debugger di Visual Studio all'istanza di DbLens.Mcp in esecuzione.
# Se ci sono piu' istanze mostra un menu per scegliere quella giusta.

$ErrorActionPreference = "Stop"

# ── 1. Trova i processi DbLens.Mcp ───────────────────────────────────────────

$processes = @(Get-Process -Name "DbLens.Mcp" -ErrorAction SilentlyContinue)

if ($processes.Count -eq 0) {
    Write-Host "Nessun processo DbLens.Mcp trovato. Avvia prima il server con 'make dev-server'." -ForegroundColor Red
    exit 1
}

# ── 2. Se ce ne sono piu' di uno, mostra un menu ─────────────────────────────

$target = $null

if ($processes.Count -eq 1) {
    $target = $processes[0]
    Write-Host "Trovato: PID $($target.Id)  $($target.Path)" -ForegroundColor Cyan
} else {
    Write-Host ""
    Write-Host "Piu' istanze trovate - scegli quella da debuggare:" -ForegroundColor Yellow
    Write-Host ""

    for ($i = 0; $i -lt $processes.Count; $i++) {
        $p = $processes[$i]
        $ageMin = [int]((Get-Date) - $p.StartTime).TotalMinutes

        if ($p.Path -like "*\publish\*") {
            $label = "[publish]"
        } else {
            $label = "[debug]  "
        }

        Write-Host "  [$($i+1)] PID $($p.Id)  $label  avviato $ageMin min fa  $($p.Path)" -ForegroundColor White
    }

    Write-Host ""
    $choice = Read-Host "Scelta (1-$($processes.Count))"
    $idx = [int]$choice - 1

    if ($idx -lt 0 -or $idx -ge $processes.Count) {
        Write-Host "Scelta non valida." -ForegroundColor Red
        exit 1
    }

    $target = $processes[$idx]
}

# ── 3. Recupera l'istanza di Visual Studio aperta tramite DTE ─────────────────

Write-Host ""
Write-Host "Connessione a Visual Studio..." -ForegroundColor Cyan

$dte = $null
$vsVersions = @("VisualStudio.DTE.18.0", "VisualStudio.DTE.17.0", "VisualStudio.DTE")

foreach ($ver in $vsVersions) {
    try {
        $dte = [System.Runtime.InteropServices.Marshal]::GetActiveObject($ver)
        Write-Host "Trovato: $ver" -ForegroundColor Green
        break
    } catch {
        # versione non in esecuzione, prova la prossima
    }
}

if ($null -eq $dte) {
    Write-Host "Visual Studio non e' aperto. Aprilo con la soluzione dblens.slnx prima di eseguire 'make attach'." -ForegroundColor Red
    exit 1
}

# ── 4. Attach ─────────────────────────────────────────────────────────────────

$vsProcess = $null

foreach ($p in $dte.Debugger.LocalProcesses) {
    if ($p.ProcessID -eq $target.Id) {
        $vsProcess = $p
        break
    }
}

if ($null -eq $vsProcess) {
    Write-Host "Processo PID $($target.Id) non trovato nella lista di VS. Riprova tra qualche secondo." -ForegroundColor Red
    exit 1
}

$vsProcess.Attach()

Write-Host ""
Write-Host "Debugger attaccato al PID $($target.Id)." -ForegroundColor Green
Write-Host "Imposta i breakpoint in Visual Studio e chiama un tool per triggerarli." -ForegroundColor White
