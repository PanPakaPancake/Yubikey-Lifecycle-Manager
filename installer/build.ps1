# Baut YubikeyLifecycleManager-Agent.msi (Windows, .NET 10 SDK erforderlich).
#   pwsh installer/build.ps1 [-Version 0.1.0]
param([string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $PSScriptRoot "publish"
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

$common = @("-c", "Release", "-r", "win-x64", "--self-contained", "true",
            "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:Version=$Version")
function Invoke-Checked { & $args[0] $args[1..($args.Count - 1)]; if ($LASTEXITCODE -ne 0) { throw "Fehlgeschlagen: $($args -join ' ')" } }
Invoke-Checked dotnet publish (Join-Path $root "src/Ylm.Agent/Ylm.Agent.csproj") @common -o (Join-Path $publish "agent")
Invoke-Checked dotnet publish (Join-Path $root "src/Ylm.Agent.Tray/Ylm.Agent.Tray.csproj") @common -o (Join-Path $publish "tray")

Invoke-Checked dotnet build (Join-Path $PSScriptRoot "Ylm.Agent.Installer.wixproj") -c Release -p:ProductVersion=$Version "-p:PublishDir=$publish\" -o (Join-Path $PSScriptRoot "out")
Write-Host "MSI: $(Join-Path $PSScriptRoot 'out\YubikeyLifecycleManager-Agent.msi')"
