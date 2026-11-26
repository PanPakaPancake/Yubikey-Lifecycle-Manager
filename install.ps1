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
  (Standard)       = nur dieser PC unter https://localhost (interne Zertifizierungsstelle, wird in Windows eingetragen)
  local            = nur dieser PC unter http://localhost:8080, ohne HTTPS
  internal         = internes Netz/ZTNA, HTTPS mit interner Zertifizierungsstelle von Caddy
  cert             = internes Netz/ZTNA, HTTPS mit eigenem Zertifikat (-Cert, -Key, z. B. Firmen-PKI)
  proxy            = hinter einem vorhandenen Reverse Proxy oder ZTNA-Connector
  https            = öffentliche Domain mit Let's Encrypt (nur wenn aus dem Internet erreichbar), Ports 80/443 nötig
.PARAMETER Domain
  DNS-Name für -Mode internal, cert und https.
.PARAMETER Cert
  Zertifikat (PEM, mit Zwischenzertifikaten) für -Mode cert.
.PARAMETER Key
  Privater Schlüssel (PEM, unverschlüsselt) für -Mode cert.
.PARAMETER Url
  Öffentliche HTTPS-Adresse für -Mode proxy.
.PARAMETER Version
  Bestimmte Version (z. B. v0.3.0) statt der neuesten.
#>
[CmdletBinding()]
param(
    [string]$Dir = (Join-Path $env:USERPROFILE 'YubikeyLifecycleManager'),
    [ValidateSet('', 'local', 'internal', 'cert', 'https', 'proxy')][string]$Mode = '',
    [string]$Domain = '',
    [string]$Cert = '',
    [string]$Key = '',
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

# Fragt fehlende Angaben zum Modus ab und setzt $Url (und $Domain).
function Resolve-Mode {
    switch ($script:Mode) {
        { $_ -in 'internal', 'cert', 'https' } {
            if (-not $script:Domain) { $script:Domain = Read-Host 'DNS-Name der Plattform (z. B. ylm.firma.local)' }
            if (-not $script:Domain) { Fail 'Ohne DNS-Namen kein HTTPS.' }
            $script:Domain = ($script:Domain -replace '^https://', '') -replace '/.*$', ''
            $script:Url = "https://$($script:Domain)"
            if ($script:Mode -eq 'cert') {
                if (-not $script:Cert -or -not (Test-Path $script:Cert)) { Fail '-Mode cert braucht -Cert (PEM mit Zwischenzertifikaten).' }
                if (-not $script:Key -or -not (Test-Path $script:Key)) { Fail '-Mode cert braucht -Key (PEM, unverschlüsselt).' }
                Copy-Item $script:Cert (Join-Path $Dir 'certs\ylm.crt') -Force
                Copy-Item $script:Key (Join-Path $Dir 'certs\ylm.key') -Force
            }
        }
        'proxy' {
            if (-not $script:Url) { $script:Url = Read-Host 'Adresse, unter der Browser und Agents die Plattform erreichen (https://...)' }
            if ($script:Url -notmatch '^https://') { Fail 'Die Adresse muss mit https:// beginnen.' }
            $script:Url = $script:Url.TrimEnd('/')
        }
        default { $script:Url = 'http://localhost:8080' }
    }
}
# Schreibt den Modus in .env (dieselben Werte wie install.sh).
function Write-Mode {
    Set-EnvValue $envFile 'YLM_PUBLIC_URL' $script:Url
    if ($script:Mode -in 'internal', 'cert', 'https') {
        Set-EnvValue $envFile 'COMPOSE_PROFILES' 'https'
        Set-EnvValue $envFile 'YLM_DOMAIN' $script:Domain
        $tls = switch ($script:Mode) { 'internal' { 'tls internal' } 'cert' { 'tls /certs/ylm.crt /certs/ylm.key' } default { '' } }
        Set-EnvValue $envFile 'YLM_CADDY_TLS' $tls
        # Nur auf diesem PC: Caddy nicht im Netz anbieten.
        if ($script:Domain -eq 'localhost') { Set-EnvValue $envFile 'YLM_HTTPS_BIND' '127.0.0.1' }
    } else {
        Set-EnvValue $envFile 'COMPOSE_PROFILES' ''
    }
    Set-EnvValue $envFile 'YLM_MODE' $script:Mode
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

New-Item -ItemType Directory -Force -Path (Join-Path $Dir 'deploy'), (Join-Path $Dir 'backups'), (Join-Path $Dir 'certs') -ErrorAction Stop | Out-Null
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
$passphrase = ''
if (Test-Path $envFile) {
    Info 'Vorhandene .env wird beibehalten.'
    if (-not (Get-EnvValue $envFile 'UPDATER_TOKEN')) { Set-EnvValue $envFile 'UPDATER_TOKEN' (New-Secret 32); Info 'UPDATER_TOKEN ergänzt (Update per Klick).' }
    if (-not (Get-EnvValue $envFile 'BACKUP_PASSPHRASE')) {
        $passphrase = New-Secret 24; Set-EnvValue $envFile 'BACKUP_PASSPHRASE' $passphrase
        Info 'BACKUP_PASSPHRASE ergänzt: neue Sicherungen werden verschlüsselt (ältere bleiben lesbar).'
    }
    if ($Mode -or $Domain) {
        if (-not $Mode) { $Mode = 'https' }
        Resolve-Mode
        Write-Mode
        Info "Erreichbarkeit umgestellt auf: $Mode ($Url)."
    }
} else {
    $running = $false
    try { Invoke-WebRequest 'http://127.0.0.1:8080/healthz' -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop | Out-Null; $running = $true } catch { }
    if ($running) { Fail "Auf Port 8080 läuft schon eine Installation (anderer Ordner?). Bitte im Ordner der vorhandenen Installation arbeiten oder sie vorher stoppen (docker compose down)." }
    # Standard: HTTPS auch auf dem eigenen PC (https://localhost, interne Zertifizierungsstelle von Caddy).
    if (-not $Mode) { if ($Domain) { $Mode = 'https' } elseif ($Url) { $Mode = 'proxy' } else { $Mode = 'internal'; $Domain = 'localhost' } }
    Resolve-Mode
    $passphrase = New-Secret 24
    $lines = @(
        "# Angelegt von install.ps1 am $(Get-Date -Format 'yyyy-MM-dd HH:mm'). Erklärung aller Werte: .env.example",
        'COMPOSE_PROJECT_NAME=ylm',
        "POSTGRES_PASSWORD=$(New-Secret 24)",
        "YLM_PUBLIC_URL=$Url",
        "UPDATER_TOKEN=$(New-Secret 32)",
        "BACKUP_PASSPHRASE=$passphrase",
        'TZ=Europe/Berlin'
    )
    [IO.File]::WriteAllText($envFile, (($lines -join "`n") + "`n"), $Utf8)
    Write-Mode
    Info ".env mit neuen Zufallsgeheimnissen angelegt ($envFile)."
}
$Url = Get-EnvValue $envFile 'YLM_PUBLIC_URL'
# Bei erneutem Lauf (z. B. nach Änderungen unter Adresse und Zertifikat) den Stand aus .env nehmen.
if (-not $Mode) { $Mode = Get-EnvValue $envFile 'YLM_MODE' }
if (-not $Domain) { $Domain = Get-EnvValue $envFile 'YLM_DOMAIN' }
$openSetup = $false

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
    if ($Url -like 'http://localhost*' -or $Url -eq 'https://localhost') { $openSetup = $true }
}
if ($passphrase) {
    Write-Host ''
    Warn 'Passphrase der Datensicherung (BACKUP_PASSPHRASE in .env). Bitte jetzt in den Passwortmanager übernehmen:'
    Write-Host ("      " + $passphrase) -ForegroundColor White
    Info 'Ohne sie lässt sich keine Sicherung einspielen, auch nicht, wenn dieser Rechner verloren ist.'
    Write-Host ''
}
if ($Mode -eq 'internal') {
    $ca = Join-Path $Dir 'ylm-root-ca.crt'
    # Bei jedem Lauf frisch aus Caddy holen: eine alte Datei kann zu einer neu erzeugten CA nicht mehr passen.
    for ($i = 0; $i -lt 10; $i++) {
        $pem = & docker compose exec -T caddy cat /data/caddy/pki/authorities/local/root.crt 2>$null
        if ($LASTEXITCODE -eq 0 -and $pem) { [IO.File]::WriteAllText($ca, (($pem -join "`n") + "`n"), $Utf8); break } else { Start-Sleep -Seconds 2 }
    }
    if ((Test-Path $ca) -and $Domain -eq 'localhost') {
        # Auf dem eigenen PC: Stammzertifikat im Computer-Speicher eintragen (eine UAC-Abfrage), damit Browser und
        # der Agent-Dienst (läuft als SYSTEM) https://localhost ohne Warnung vertrauen.
        $thumb = (New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 $ca).Thumbprint
        if (-not (Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq $thumb)) {
            Info 'Trage das Stammzertifikat in Windows ein (Bestätigung per UAC) ...'
            try { Start-Process certutil -ArgumentList '-addstore', 'Root', "`"$ca`"" -Verb RunAs -Wait -WindowStyle Hidden } catch { Warn "Nicht eingetragen: $($_.Exception.Message)" }
        }
        if (Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq $thumb) { Info 'Stammzertifikat eingetragen: https://localhost ist vertrauenswürdig.' }
        else { Warn "Bitte als Administrator ausführen: certutil -addstore Root `"$ca`"" }
    }
    if (Test-Path $ca) {
        Info "Stammzertifikat der internen CA: $ca"
        Info 'Auf den Admin-PCs als vertrauenswürdige Stammzertifizierungsstelle verteilen (GPO oder Intune), einzeln: certutil -addstore Root ylm-root-ca.crt'
    } else { Warn 'Stammzertifikat noch nicht verfügbar. Später: docker compose exec -T caddy cat /data/caddy/pki/authorities/local/root.crt' }
}
Info "Installationsordner: $Dir (Einstellungen in .env, Sicherungen in backups\)"
Info 'Status: docker compose ps   Protokoll: docker compose logs -f server'
Info 'Updates: in der Oberfläche unter Globale Einstellungen -> Version und Updates, oder dieses Skript erneut ausführen.'
if ($openSetup) { Start-Process "$Url/setup" }
