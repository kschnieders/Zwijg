# Zwijg aktualisieren. Im Ordner von Zwijg in PowerShell ausführen:
#   powershell -ExecutionPolicy Bypass -File update.ps1
# Der Ordner data (Einstellungen, Protokoll, Verlauf, Schlüssel) bleibt erhalten und wird vorher gesichert.
param([string]$Repository = "kschnieders/zwijg")
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$dir = $PSScriptRoot

$current = if (Test-Path "$dir\VERSION") { (Get-Content "$dir\VERSION" -Raw).Trim() } else { "0.0.0" }
$release = Invoke-RestMethod "https://api.github.com/repos/$Repository/releases/latest" -Headers @{ "User-Agent" = "Zwijg-Update" }
$latest = $release.tag_name.TrimStart("v")

if ([version]$latest -le [version]$current) {
    Write-Host "Zwijg $current ist aktuell."
    exit 0
}

$asset = $release.assets | Where-Object { $_.name -eq "zwijg-$latest-win-x64.zip" }
if (-not $asset) { throw "Im Release $latest fehlt das Windows Paket." }
Write-Host "Update von $current auf $latest"

# Ein laufendes Zwijg aus diesem Ordner beenden, sonst sind Dateien gesperrt
$running = Get-Process Zwijg.Gateway -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dir\*" }
if ($running) {
    Write-Host "Beende laufendes Zwijg"
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}

if (Test-Path "$dir\data") {
    $backup = "$dir\data-sicherung-$current-$(Get-Date -Format yyyyMMdd-HHmmss).zip"
    Compress-Archive -Path "$dir\data" -DestinationPath $backup
    Write-Host "Daten gesichert in $backup"
}

$tmp = Join-Path ([IO.Path]::GetTempPath()) "zwijg-update-$latest"
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
New-Item $tmp -ItemType Directory | Out-Null
Invoke-WebRequest $asset.browser_download_url -OutFile "$tmp\zwijg.zip"
Expand-Archive "$tmp\zwijg.zip" "$tmp\neu"

# Alles ersetzen außer data und appsettings.json. Die wird nur beim ersten Start gelesen, eigene Änderungen bleiben so erhalten.
Get-ChildItem "$tmp\neu" | Where-Object { $_.Name -notin @("data", "appsettings.json") } |
    Copy-Item -Destination $dir -Recurse -Force
Remove-Item $tmp -Recurse -Force

Write-Host "Fertig. Zwijg $latest ist installiert, bitte wieder starten."
