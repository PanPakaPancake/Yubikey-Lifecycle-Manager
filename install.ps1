<#
.SYNOPSIS
  Installiert oder aktualisiert Yubikey Lifecycle Manager mit Docker Desktop (Windows).

.DESCRIPTION
  Lädt die Compose-Dateien der neuesten Version, legt eine .env mit Zufallsgeheimnissen an,
  holt die fertigen Images und startet die Plattform. Erneut ausführen ist gefahrlos:
  Eine vorhandene .env bleibt erhalten, Dateien und Images werden aktualisiert.

  Aufruf (PowerShell):
    irm https://raw.githubusercontent.com/PanPakaPancake/Yubikey-Lifecycle-Manager/main/install.ps1 | iex
  Mit Optionen:
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/PanPakaPancake/Yubikey-Lifecycle-Manager/main/install.ps1))) -Dir D:\ylm

.PARAMETER Dir
  Installationsordner (Standard: %USERPROFILE%\YubikeyLifecycleManager).
.PARAMETER Mode
  local (Standard) = nur dieser PC unter http://localhost:8080
  https            = eigene Domain mit automatischem HTTPS (Caddy + Let's Encrypt), Ports 80/443 nötig
  proxy            = hinter einem vorhandenen Reverse Proxy
.PARAMETER Domain
  Domain für -Mode https.
.PARAMETER Url
  Öffentliche HTTPS-Adresse für -Mode proxy.
.PARAMETER Version
  Bestimmte Version (z. B. v0.3.0) statt der neuesten.
#>
[CmdletBinding()]
param(
    [string]$Dir = (Join-Path $env:USERPROFILE 'YubikeyLifecycleManager'),
    [ValidateSet('', 'local', 'https', 'proxy')][string]$Mode = '',
    [string]$Domain = '',
    [string]$Url = '',
    [string]$Version = ''
)

# 'Continue', weil Windows PowerShell 5.1 sonst Statusausgaben von docker auf stderr als Fehler wertet.
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Repo = 'PanPakaPancake/Yubikey-Lifecycle-Manager'
$Raw = if ($env:YLM_RAW_URL) { $env:YLM_RAW_URL } else { "https://raw.githubusercontent.com/$Repo" }
$Utf8 = New-Object System.Text.UTF8Encoding($false)

function Info($text) { Write-Host "  $text" }
function Warn($text) { Write-Host "! $text" -ForegroundColor Yellow }
function Fail($text) { Write-Host "Fehler: $text" -ForegroundColor Red; throw $text }
function New-Secret([int]$bytes) {
    $buffer = New-Object byte[] $bytes
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($buffer); $rng.Dispose()
    -join ($buffer | ForEach-Object { $_.ToString('x2') })
}
function Get-EnvValue($path, $key) {
    if (-not (Test-Path $path)) { return '' }
    $line = Get-Content $path | Where-Object { $_ -match "^$key=" } | Select-Object -Last 1
    if ($line) { return $line.Substring($key.Length + 1).Trim() } else { return '' }
}
function Set-EnvValue($path, $key, $value) {
    $lines = @(Get-Content $path)
    $found = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match "^$key=") { $lines[$i] = "$key=$value"; $found = $true }
    }
    if (-not $found) { $lines += "$key=$value" }
    [IO.File]::WriteAllText($path, (($lines -join "`n") + "`n"), $Utf8)
}
function Invoke-Docker {
    & docker @args
    if ($LASTEXITCODE -ne 0) { throw "docker $($args -join ' ') fehlgeschlagen (Exitcode $LASTEXITCODE)" }
}

Write-Host 'Yubikey Lifecycle Manager - Installation' -ForegroundColor Cyan

# --- 1. Voraussetzungen ---
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Fail 'Docker fehlt. Bitte Docker Desktop installieren (https://www.docker.com/products/docker-desktop/) und neu starten.'
}
& docker info *> $null
if ($LASTEXITCODE -ne 0) { Fail 'Docker läuft nicht. Bitte Docker Desktop starten und warten, bis es bereit ist.' }
& docker compose version *> $null
if ($LASTEXITCODE -ne 0) { Fail 'docker compose fehlt. Bitte Docker Desktop aktualisieren.' }
Info ("Docker " + (& docker version --format '{{.Server.Version}}'))

# --- 2. Version und Dateien ---
if (-not $Version) {
    try { $Version = (Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -UseBasicParsing -ErrorAction Stop).tag_name }
    catch { Warn 'Neueste Version nicht ermittelbar, verwende den Stand von main.'; $Version = 'main' }
}
Info "Version: $Version"

New-Item -ItemType Directory -Force -Path (Join-Path $Dir 'deploy'), (Join-Path $Dir 'backups') -ErrorAction Stop | Out-Null
Set-Location $Dir -ErrorAction Stop
if (Test-Path (Join-Path $Dir '.git')) {
    Info "$Dir ist ein Git-Checkout: Dateien kommen aus Git (git pull), es wird nichts heruntergeladen."
} else {
    foreach ($f in 'docker-compose.yml', 'deploy/Caddyfile', 'deploy/backup.sh', '.env.example') {
        $target = Join-Path $Dir $f
        $tmp = "$target.download"
        try { Invoke-WebRequest "$Raw/$Version/$f" -OutFile $tmp -UseBasicParsing -ErrorAction Stop }
        catch {
            # Ältere Releases enthalten noch nicht alle Dateien: dann den Stand von main nehmen.
            try { Invoke-WebRequest "$Raw/main/$f" -OutFile $tmp -UseBasicParsing -ErrorAction Stop; Warn "$f gibt es in $Version noch nicht, verwende den Stand von main." }
            catch { Remove-Item $tmp -ErrorAction SilentlyContinue; Fail "Download von $f fehlgeschlagen: $($_.Exception.Message)" }
        }
        if ((Test-Path $target) -and ((Get-FileHash $target).Hash -ne (Get-FileHash $tmp).Hash)) {
            Copy-Item $target "$target.bak" -Force
            Info "$f aktualisiert (vorherige Fassung: $f.bak)"
        }
        Move-Item $tmp $target -Force
    }
}

# --- 3. .env ---
$envFile = Join-Path $Dir '.env'
if (Test-Path $envFile) {
    Info 'Vorhandene .env wird beibehalten.'
    if (-not (Get-EnvValue $envFile 'UPDATER_TOKEN')) { Set-EnvValue $envFile 'UPDATER_TOKEN' (New-Secret 32); Info 'UPDATER_TOKEN ergänzt (Update per Klick).' }
    if ($Domain) {
        Set-EnvValue $envFile 'COMPOSE_PROFILES' 'https'; Set-EnvValue $envFile 'YLM_DOMAIN' $Domain; Set-EnvValue $envFile 'YLM_PUBLIC_URL' "https://$Domain"
        Info "HTTPS für $Domain eingestellt."
    }
} else {
    $running = $false
    try { Invoke-WebRequest 'http://127.0.0.1:8080/healthz' -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop | Out-Null; $running = $true } catch { }
    if ($running) { Fail "Auf Port 8080 läuft schon eine Installation (anderer Ordner?). Bitte im Ordner der vorhandenen Installation arbeiten oder sie vorher stoppen (docker compose down)." }
    if (-not $Mode) { $Mode = if ($Domain) { 'https' } elseif ($Url) { 'proxy' } else { 'local' } }
    switch ($Mode) {
        'https' {
            if (-not $Domain) { $Domain = Read-Host 'Domain (DNS-Eintrag muss auf diesen Rechner zeigen)' }
            if (-not $Domain) { Fail 'Ohne Domain kein HTTPS.' }
            $Domain = ($Domain -replace '^https://', '') -replace '/.*$', ''
            $Url = "https://$Domain"
        }
        'proxy' {
            if (-not $Url) { $Url = Read-Host 'Öffentliche HTTPS-Adresse (z. B. https://ylm.example.com)' }
            if ($Url -notmatch '^https://') { Fail 'Die öffentliche Adresse muss mit https:// beginnen.' }
            $Url = $Url.TrimEnd('/')
        }
        default { $Url = 'http://localhost:8080' }
    }
    $lines = @(
        "# Angelegt von install.ps1 am $(Get-Date -Format 'yyyy-MM-dd HH:mm'). Erklärung aller Werte: .env.example",
        'COMPOSE_PROJECT_NAME=ylm',
        "POSTGRES_PASSWORD=$(New-Secret 24)",
        "YLM_PUBLIC_URL=$Url",
        "UPDATER_TOKEN=$(New-Secret 32)",
        'TZ=Europe/Berlin'
    )
    if ($Mode -eq 'https') { $lines += 'COMPOSE_PROFILES=https'; $lines += "YLM_DOMAIN=$Domain" }
    [IO.File]::WriteAllText($envFile, (($lines -join "`n") + "`n"), $Utf8)
    Info ".env mit neuen Zufallsgeheimnissen angelegt ($envFile)."
}
$Url = Get-EnvValue $envFile 'YLM_PUBLIC_URL'

# --- 4. Starten ---
Write-Host ''
Write-Host 'Lade Images und starte ...' -ForegroundColor Cyan
& docker compose pull
if ($LASTEXITCODE -ne 0) { Warn 'Nicht alle Images ließen sich laden. Es wird mit den vorhandenen weitergemacht.' }
Invoke-Docker compose up -d --remove-orphans

Write-Host -NoNewline '  Warte auf den Server'
$ok = $false
for ($i = 0; $i -lt 60; $i++) {
    try { Invoke-WebRequest 'http://127.0.0.1:8080/healthz' -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop | Out-Null; $ok = $true; break }
    catch { Write-Host -NoNewline '.'; Start-Sleep -Seconds 3 }
}
Write-Host ''
if (-not $ok) { Warn "Der Server antwortet noch nicht. Protokoll: cd `"$Dir`"; docker compose logs server"; return }

Write-Host ''
Write-Host "Fertig. Die Plattform läuft unter $Url" -ForegroundColor Green
$token = (& docker compose logs server 2>$null | Select-String 'Setup-Token:\s*(\S+)' | Select-Object -Last 1)
if ($token) {
    Info "Einrichtung: $Url/setup öffnen und dieses Setup-Token eingeben:"
    Write-Host ''
    Write-Host ("      " + $token.Matches[0].Groups[1].Value) -ForegroundColor White
    Write-Host ''
    if ($Url -like 'http://localhost*') { Start-Process "$Url/setup" }
}
Info "Installationsordner: $Dir (Einstellungen in .env, Sicherungen in backups\)"
Info 'Status: docker compose ps   Protokoll: docker compose logs -f server'
Info 'Updates: in der Oberfläche unter Globale Einstellungen -> Version und Updates, oder dieses Skript erneut ausführen.'
