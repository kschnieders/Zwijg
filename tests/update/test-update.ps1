# Spielt Updates mit update.ps1 durch: gelungenes Update, kaputtes Paket mit automatischer Rückkehr und Zurück von Hand.
# Aufruf: powershell -File test-update.ps1 -Programm <Ordner mit veröffentlichtem Zwijg> -Skript <update.ps1>
param([string]$Programm, [string]$Skript)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$work = Join-Path ([IO.Path]::GetTempPath()) "zwijg-updatetest-$(Get-Random)"
$dir = "$work\zwijg"

function Check($ok, [string]$text) {
    if (-not $ok) { throw "FEHLER: $text" }
    Write-Host "ok: $text"
}

function Installed { (Get-Content "$dir\VERSION" -Raw).Trim() }

# Paket bauen wie im Release: Programm, VERSION und update.ps1 in einer ZIP
function New-Package([string]$version, [switch]$Broken) {
    $folder = "$work\paket-$version"
    Copy-Item $Programm $folder -Recurse
    Set-Content "$folder\VERSION" $version
    Copy-Item $Skript "$folder\update.ps1"
    # Kaputtes Paket: Das Programm ist da, startet aber nicht
    if ($Broken) { Set-Content "$folder\Zwijg.Gateway.dll" "kaputt" }
    Compress-Archive "$folder\*" "$work\zwijg-$version.zip"
    "$work\zwijg-$version.zip"
}

function Run-Update([string[]]$arguments) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File "$dir\update.ps1" @arguments | ForEach-Object { Write-Host "  | $_" }
    $LASTEXITCODE
}

New-Item $work -ItemType Directory | Out-Null
try {
    $old = New-Package "0.0.1"
    $new = New-Package "0.0.2"
    $broken = New-Package "0.0.3" -Broken

    # Alte Version installieren und einmal starten, damit Daten entstehen
    Expand-Archive $old $dir
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
    $p = Start-Process "$dir\Zwijg.Gateway.exe" -ArgumentList "--urls=http://127.0.0.1:$port" -WorkingDirectory $dir -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput "$work\erststart.log" -RedirectStandardError "$work\erststart.fehler"
    $up = $false
    for ($i = 0; $i -lt 60 -and -not $up; $i++) { Start-Sleep 1; try { $up = (Invoke-RestMethod "http://127.0.0.1:$port/health").status -eq "ok" } catch { } }
    Stop-Process -Id $p.Id -Force; $p.WaitForExit(); Start-Sleep 2
    Check $up "Alte Version startet"
    Check (Test-Path "$dir\data\settings.json") "Daten sind angelegt"
    Set-Content "$dir\data\notiz.txt" "vor dem Update"

    Check ((Run-Update @("-Paket", $new)) -eq 0) "Update auf 0.0.2 läuft durch"
    Check ((Installed) -eq "0.0.2") "0.0.2 ist installiert"
    Check ((Get-ChildItem "$dir\update-sicherungen" -Directory).Count -eq 1) "Eine Sicherung ist angelegt"
    Check ((Get-Content "$dir\data\notiz.txt") -eq "vor dem Update") "Daten sind unverändert"
    Check (-not (Test-Path "$dir\data\sicherungen")) "Beim Probestart keine zweite Sicherung von Zwijg selbst"

    Set-Content "$dir\data\notiz.txt" "nach dem Update"
    Check ((Run-Update @("-Paket", $broken)) -eq 1) "Kaputtes Paket wird abgelehnt"
    Check ((Installed) -eq "0.0.2") "0.0.2 ist wieder installiert"
    Check ((Get-Content "$dir\data\notiz.txt") -eq "nach dem Update") "Daten sind wie vor dem kaputten Update"

    Check ((Run-Update @("-Paket", $old)) -eq 0) "Älteres Paket wird nicht installiert"
    Check ((Installed) -eq "0.0.2") "0.0.2 bleibt installiert"

    # Zurück nimmt die neueste Sicherung, also den Stand vor dem kaputten Update
    Check ((Run-Update @("-Zurueck")) -eq 0) "Zurück läuft durch"
    Check ((Installed) -eq "0.0.2") "Stand vor dem kaputten Update"

    Check ((Run-Update @("-Zurueck", "-Sicherung", (Get-ChildItem "$dir\update-sicherungen" -Directory | Where-Object Name -like "*-0.0.1" | Select-Object -First 1).Name)) -eq 0) "Zurück auf eine bestimmte Sicherung"
    Check ((Installed) -eq "0.0.1") "0.0.1 ist wieder installiert"
    Check ((Get-Content "$dir\data\notiz.txt") -eq "vor dem Update") "Daten von vor dem ersten Update"

    Write-Host "Alle Prüfungen bestanden" -ForegroundColor Green
} finally {
    Get-Process Zwijg.Gateway -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$work\*" } | Stop-Process -Force
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
