# Zwijg aktualisieren. Im Ordner von Zwijg in PowerShell ausführen:
#   powershell -ExecutionPolicy Bypass -File update.ps1
# Ohne Internet mit einem heruntergeladenen Paket:
#   powershell -ExecutionPolicy Bypass -File update.ps1 -Paket C:\Downloads\zwijg-1.2.0-win-x64.zip
# Zurück auf den Stand vor dem letzten Update:
#   powershell -ExecutionPolicy Bypass -File update.ps1 -Zurueck
#
# Vor dem Update kommen Programm und Daten zusammen in den Ordner update-sicherungen. Danach startet die neue
# Version einmal zur Probe. Klappt das nicht, wird automatisch der alte Stand wiederhergestellt.
param(
    [string]$Repository = "kschnieders/zwijg",
    [string]$Paket,
    [switch]$Zurueck,
    [string]$Sicherung
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$dir = $PSScriptRoot
$backups = Join-Path $dir "update-sicherungen"
$exe = "Zwijg.Gateway.exe"
$keepBackups = 3

# Bleibt beim Update, wie es ist. appsettings.json wird nur beim ersten Start gelesen, eigene Änderungen bleiben so erhalten.
$untouched = @("data", "update-sicherungen", "appsettings.json")

# Nicht mitsichern: Sprachmodelle ändert ein Update nicht, und die Sicherungen von Zwijg selbst sind schon Sicherungen
$dataSkip = @("models", "sicherungen")

function Get-Version([string]$folder) {
    if (Test-Path "$folder\VERSION") { (Get-Content "$folder\VERSION" -Raw).Trim() } else { "0.0.0" }
}

function Stop-Zwijg {
    $running = Get-Process Zwijg.Gateway -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dir\*" }
    if ($running) {
        Write-Host "Beende laufendes Zwijg"
        $running | Stop-Process -Force
        Start-Sleep -Seconds 2
    }
}

# Programm und Daten in einen neuen Ordner unter update-sicherungen kopieren
function New-Backup([string]$suffix) {
    $name = "$(Get-Date -Format yyyyMMdd-HHmmss)-$(Get-Version $dir)$suffix"
    $target = Join-Path $backups $name
    # Zwei Sicherungen in derselben Sekunde bekommen eine Nummer dran
    for ($n = 2; Test-Path $target; $n++) { $target = Join-Path $backups "$name-$n" }
    New-Item "$target\programm" -ItemType Directory -Force | Out-Null
    Get-ChildItem $dir -Force | Where-Object { $_.Name -notin $untouched } | Copy-Item -Destination "$target\programm" -Recurse
    if (Test-Path "$dir\data") {
        New-Item "$target\data" -ItemType Directory | Out-Null
        Get-ChildItem "$dir\data" -Force | Where-Object { $_.Name -notin $dataSkip } | Copy-Item -Destination "$target\data" -Recurse
    }
    $target
}

# Programm und Daten aus einer Sicherung zurückholen. Die Sicherung selbst bleibt liegen.
function Restore-Backup([string]$source) {
    Get-ChildItem "$source\programm" -Force | Copy-Item -Destination $dir -Recurse -Force
    if (Test-Path "$source\data") {
        Get-ChildItem "$dir\data" -Force | Where-Object { $_.Name -notin $dataSkip } | Remove-Item -Recurse -Force
        Get-ChildItem "$source\data" -Force | Copy-Item -Destination "$dir\data" -Recurse -Force
    }
}

# Neue Version einmal auf einem freien Port starten und warten, bis sie antwortet
function Test-Start {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()

    $log = Join-Path ([IO.Path]::GetTempPath()) "zwijg-probestart.log"
    $process = Start-Process "$dir\$exe" -ArgumentList "--urls=http://127.0.0.1:$port", "--Zwijg:SkipUpdateSnapshot=true" `
        -WorkingDirectory $dir -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.fehler"
    try {
        for ($i = 0; $i -lt 60 -and -not $process.HasExited; $i++) {
            Start-Sleep -Seconds 1
            try {
                if ((Invoke-RestMethod "http://127.0.0.1:$port/health" -TimeoutSec 2).status -eq "ok") { return $true }
            } catch { }
        }
        Write-Host "Ausgabe der neuen Version:"
        Get-Content $log, "$log.fehler" -Tail 20 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
        return $false
    } finally {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force; $process.WaitForExit() }
        # Windows gibt die Datenbanken erst kurz nach dem Beenden frei
        Start-Sleep -Seconds 2
    }
}

# Zurück auf den Stand vor dem letzten Update
if ($Zurueck) {
    $all = Get-ChildItem $backups -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending
    $source = if ($Sicherung) { $all | Where-Object Name -eq $Sicherung } else { $all | Where-Object Name -notlike "*-vor-zurueck" | Select-Object -First 1 }
    if (-not $source) { throw "Keine passende Sicherung in $backups gefunden." }

    Stop-Zwijg
    # Auch den jetzigen Stand sichern, damit sich das Zurückgehen wieder rückgängig machen lässt
    $before = New-Backup "-vor-zurueck"
    Restore-Backup $source.FullName
    Write-Host "Fertig. Zwijg $(Get-Version $dir) aus $($source.Name) ist wiederhergestellt, bitte wieder starten."
    Write-Host "Der Stand davor liegt in $before"
    exit 0
}

$current = Get-Version $dir
$tmp = Join-Path ([IO.Path]::GetTempPath()) "zwijg-update-$(Get-Random)"
New-Item $tmp -ItemType Directory | Out-Null
try {
    if ($Paket) {
        $zip = (Resolve-Path $Paket).Path
        $digest = $null
    } else {
        $release = Invoke-RestMethod "https://api.github.com/repos/$Repository/releases/latest" -Headers @{ "User-Agent" = "Zwijg-Update" }
        $latest = $release.tag_name.TrimStart("v")
        if ([version]$latest -le [version]$current) {
            Write-Host "Zwijg $current ist aktuell."
            exit 0
        }
        $asset = $release.assets | Where-Object { $_.name -eq "zwijg-$latest-win-x64.zip" }
        if (-not $asset) { throw "Im Release $latest fehlt das Windows Paket." }
        Write-Host "Lade Zwijg $latest herunter"
        $zip = "$tmp\zwijg.zip"
        Invoke-WebRequest $asset.browser_download_url -OutFile $zip
        $digest = $asset.digest
    }

    # Prüfsumme von GitHub, damit kein beschädigter oder unterwegs veränderter Download installiert wird
    if ($digest) {
        if ("sha256:$((Get-FileHash $zip -Algorithm SHA256).Hash.ToLower())" -ne $digest) { throw "Der Download ist beschädigt, die Prüfsumme stimmt nicht. Es wurde nichts geändert." }
        Write-Host "Prüfsumme stimmt"
    }

    # Erst auspacken und prüfen, dann erst etwas am installierten Zwijg ändern
    Expand-Archive $zip "$tmp\neu"
    if (-not (Test-Path "$tmp\neu\$exe") -or -not (Test-Path "$tmp\neu\VERSION")) { throw "Das Paket ist unvollständig. Es wurde nichts geändert." }
    $new = Get-Version "$tmp\neu"
    if ([version]$new -le [version]$current) {
        Write-Host "Das Paket enthält Zwijg $new, installiert ist schon $current. Es wurde nichts geändert."
        exit 0
    }
    Write-Host "Update von $current auf $new"

    Stop-Zwijg
    $backup = New-Backup ""
    Write-Host "Programm und Daten gesichert in $backup"

    try {
        Get-ChildItem "$tmp\neu" -Force | Where-Object { $_.Name -notin $untouched } | Copy-Item -Destination $dir -Recurse -Force

        # Ohne Daten ist es eine neue Installation, ein Probestart würde nur einen Startschlüssel erzeugen, den keiner sieht
        if (Test-Path "$dir\data\settings.json") {
            Write-Host "Starte Zwijg $new zur Probe"
            if (-not (Test-Start)) { throw "Zwijg $new ist beim Probestart nicht angelaufen." }
            Write-Host "Probestart in Ordnung"
        }
    } catch {
        Write-Host "Fehler: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "Stelle Zwijg $current wieder her"
        Restore-Backup $backup
        Write-Host "Zwijg $current ist wieder installiert, Daten wie vor dem Update. Bitte wieder starten." -ForegroundColor Yellow
        exit 1
    }

    Get-ChildItem $backups -Directory | Sort-Object Name -Descending | Select-Object -Skip $keepBackups | Remove-Item -Recurse -Force
    Write-Host "Fertig. Zwijg $new ist installiert, bitte wieder starten." -ForegroundColor Green
    Write-Host "Zurück zu $current geht mit: powershell -ExecutionPolicy Bypass -File update.ps1 -Zurueck"
} finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
