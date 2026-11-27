<#
.SYNOPSIS
  Rauchtest für das Agent-MSI auf einem Windows-PC (läuft in der CI auf dem Windows-Runner, Job "E2E: Windows-Agent").

.DESCRIPTION
  1. MSI still installieren, Dienst muss laufen.
  2. Tray-App starten, sie darf nicht abstürzen (vgl. Absturz beim Start in 0.11.1).
  3. Testserver (tests/Ylm.E2E.Host) mit HTTPS starten, Zertifikat als vertrauenswürdig eintragen.
  4. Kopplungscode holen und den installierten Dienst über die Named Pipe koppeln, genau wie die Tray-App es tut
     (vgl. SecurityException beim Koppeln in 0.11.2). Der Agent muss sich danach am Server melden.
  5. Tray-App läuft weiterhin, im Ereignisprotokoll keine Abstürze; MSI wieder deinstallieren.

  Nur auf einem Test-PC bzw. Runner ausführen: Das Skript installiert und entfernt den Agent und trägt ein
  Test-Zertifikat in die vertrauenswürdigen Stammzertifizierungsstellen ein (wird am Ende wieder entfernt).

.PARAMETER Msi
  Pfad zum MSI (Standard: installer/out/YubikeyLifecycleManager-Agent.msi).

.PARAMETER Postgres
  PostgreSQL für den Testserver, ohne Datenbankname.
#>
param(
    [string]$Msi = (Join-Path $PSScriptRoot '..\..\installer\out\YubikeyLifecycleManager-Agent.msi'),
    [string]$Postgres = 'Host=localhost;Username=postgres;Password=root',
    [int]$HttpPort = 5180,
    [int]$HttpsPort = 5443,
    [string]$LogDir = (Join-Path $PSScriptRoot 'logs')
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Msi = (Resolve-Path $Msi).Path
New-Item -ItemType Directory -Force $LogDir | Out-Null
$serviceName = 'YubikeyLifecycleManagerAgent'
$agentDir = Join-Path $env:ProgramFiles 'Yubikey Lifecycle Manager\Agent'
$dataDir = Join-Path $env:ProgramData 'YubikeyLifecycleManager\Agent'
$started = Get-Date
$hostProcess = $null
$certThumbprint = $null

function Step([string]$text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Fail([string]$text) { throw "FEHLER: $text" }

function Wait-Until([scriptblock]$condition, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $condition) { return }
        Start-Sleep -Milliseconds 500
    }
    Fail "Zeitüberschreitung: $what"
}

function Assert-TrayAlive {
    $tray = Get-Process YlmTray -ErrorAction SilentlyContinue
    if (-not $tray) { Fail 'Die Tray-App läuft nicht mehr (abgestürzt?).' }
}

function Get-CrashEvents {
    # Abstürze von Agent/Tray: "Application Error" (1000) und ".NET Runtime" (1026) im Anwendungsprotokoll.
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started } -ErrorAction SilentlyContinue |
        Where-Object { ($_.ProviderName -in 'Application Error', '.NET Runtime', 'Windows Error Reporting') -and $_.Message -match 'YlmTray|YlmAgent' }
}

try {
    Step 'MSI installieren'
    $p = Start-Process msiexec.exe -ArgumentList '/i', "`"$Msi`"", '/qn', '/norestart', '/l*v', "`"$LogDir\install.log`"" -Wait -PassThru
    if ($p.ExitCode -ne 0) { Fail "msiexec /i beendet mit $($p.ExitCode) (siehe install.log)" }
    foreach ($exe in 'YlmAgent.exe', 'YlmTray.exe') {
        if (-not (Test-Path (Join-Path $agentDir $exe))) { Fail "$exe fehlt in $agentDir" }
    }

    Step 'Dienst läuft'
    Wait-Until { (Get-Service $serviceName -ErrorAction SilentlyContinue).Status -eq 'Running' } 60 'Dienst startet nicht'
    $svc = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    Write-Host "Dienst: $($svc.State), Konto $($svc.StartName), Start $($svc.StartMode)"
    if ($svc.StartMode -ne 'Auto') { Fail "Dienst startet nicht automatisch ($($svc.StartMode))" }

    Step 'Tray-App starten'
    Start-Process (Join-Path $agentDir 'YlmTray.exe') -WorkingDirectory $agentDir | Out-Null
    Wait-Until { Get-Process YlmTray -ErrorAction SilentlyContinue } 20 'Tray-App startet nicht'
    Start-Sleep -Seconds 15
    Assert-TrayAlive
    Write-Host 'Tray-App läuft seit 15 s.'

    Step 'Testserver mit HTTPS starten'
    $cer = Join-Path $env:RUNNER_TEMP 'ylm-e2e.cer'
    if (-not $env:RUNNER_TEMP) { $cer = Join-Path $env:TEMP 'ylm-e2e.cer' }
    Remove-Item $cer -ErrorAction SilentlyContinue
    $env:YLM_E2E_PORT = "$HttpPort"
    $env:YLM_E2E_HTTPS_PORT = "$HttpsPort"
    $env:YLM_E2E_CERT_OUT = $cer
    $env:YLM_TEST_POSTGRES = $Postgres
    $hostProcess = Start-Process dotnet -ArgumentList 'run', '--project', "`"$repo\tests\Ylm.E2E.Host`"", '-c', 'Release', '--no-build' `
        -RedirectStandardOutput "$LogDir\e2e-host.log" -RedirectStandardError "$LogDir\e2e-host.err.log" -PassThru -WindowStyle Hidden
    $base = "http://localhost:$HttpPort"
    Wait-Until { try { (Invoke-WebRequest "$base/e2e/info" -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch { $false } } 180 'Testserver startet nicht (siehe e2e-host.log)'
    $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($cer)
    $certThumbprint = $cert.Thumbprint
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
    $store.Open('ReadWrite'); $store.Add($cert); $store.Close()
    $serverUrl = "https://localhost:$HttpsPort"
    $null = Invoke-WebRequest "$serverUrl/healthz" -UseBasicParsing   # Zertifikat wird vertraut

    Step 'Kopplungscode holen'
    $session = $null
    $null = Invoke-WebRequest "$base/auth/dev-login" -Method Post -ContentType 'application/json' -Headers @{ 'X-YLM-CSRF' = '1' } `
        -Body '{"upn":"owner@muster-it.example","role":"Owner"}' -SessionVariable session -UseBasicParsing
    $code = (Invoke-RestMethod "$base/api/agents/pairing-codes" -Method Post -Headers @{ 'X-YLM-CSRF' = '1' } -WebSession $session).code
    if (-not $code) { Fail 'Kein Kopplungscode erhalten' }

    Step 'Dienst über die Named Pipe koppeln (wie die Tray-App)'
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'YubikeyLifecycleManager.Agent', [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous, [System.Security.Principal.TokenImpersonationLevel]::Identification)
    $pipe.Connect(10000)
    $reader = [System.IO.StreamReader]::new($pipe)
    $writer = [System.IO.StreamWriter]::new($pipe); $writer.AutoFlush = $true
    $writer.WriteLine((@{ type = 'PairRequest'; serverUrl = $serverUrl; pairingCode = $code } | ConvertTo-Json -Compress))
    $result = $null
    $deadline = (Get-Date).AddSeconds(90)
    $task = $null
    while (-not $result -and (Get-Date) -lt $deadline) {
        if (-not $task) { $task = $reader.ReadLineAsync() }
        if (-not $task.Wait(1000)) { continue }
        $line = $task.Result; $task = $null
        if ($null -eq $line) { break }
        $msg = $line | ConvertFrom-Json
        if ($msg.type -eq 'PairResult') { $result = $msg }
    }
    $pipe.Dispose()
    if (-not $result) { Fail 'Keine Antwort des Dienstes auf die Kopplung' }
    Write-Host "Antwort: $($result.text)"
    if ($result.isError) { Fail "Kopplung fehlgeschlagen: $($result.text)" }

    Step 'Agent meldet sich am Server'
    Wait-Until {
        $agents = Invoke-RestMethod "$base/api/agents" -WebSession $session
        @($agents | Where-Object { $_.online }).Count -ge 1
    } 60 'Agent ist nach der Kopplung nicht online'
    $agent = @(Invoke-RestMethod "$base/api/agents" -WebSession $session | Where-Object { $_.online })[0]
    Write-Host "Agent online: $($agent.machineName), Version $($agent.agentVersion)"

    Step 'Nach dem Koppeln: Tray-App läuft, keine Abstürze'
    Start-Sleep -Seconds 10
    Assert-TrayAlive
    $crashes = @(Get-CrashEvents)
    if ($crashes.Count -gt 0) {
        $crashes | ForEach-Object { Write-Host "[$($_.ProviderName)] $($_.Message)" }
        Fail 'Absturz von Agent oder Tray-App im Ereignisprotokoll'
    }
    if ((Get-Service $serviceName).Status -ne 'Running') { Fail 'Dienst läuft nach dem Koppeln nicht mehr' }

    Step 'MSI deinstallieren'
    Get-Process YlmTray -ErrorAction SilentlyContinue | Stop-Process -Force
    $p = Start-Process msiexec.exe -ArgumentList '/x', "`"$Msi`"", '/qn', '/norestart', '/l*v', "`"$LogDir\uninstall.log`"" -Wait -PassThru
    if ($p.ExitCode -ne 0) { Fail "msiexec /x beendet mit $($p.ExitCode)" }
    if (Get-Service $serviceName -ErrorAction SilentlyContinue) { Fail 'Dienst ist nach der Deinstallation noch vorhanden' }

    Write-Host "`nAgent-Rauchtest bestanden." -ForegroundColor Green
}
finally {
    # Protokolle für die Fehlersuche einsammeln (Upload als Artefakt in der CI).
    if (Test-Path "$dataDir\logs") { Copy-Item "$dataDir\logs\*" $LogDir -ErrorAction SilentlyContinue }
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'Ylm|Yubikey' } | Format-List TimeCreated, ProviderName, Id, Message |
        Out-File "$LogDir\eventlog.txt" -Width 300
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    Get-Process Ylm.E2E.Host -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if ($certThumbprint) {
        $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
        $store.Open('ReadWrite')
        $store.Certificates | Where-Object Thumbprint -eq $certThumbprint | ForEach-Object { $store.Remove($_) }
        $store.Close()
    }
}
