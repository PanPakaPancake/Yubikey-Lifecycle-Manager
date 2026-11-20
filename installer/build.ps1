# Baut YubikeyLifecycleManager-Agent.msi (Windows, .NET 10 SDK erforderlich).
#   pwsh installer/build.ps1 [-Version 0.1.0] [-Stage all|publish|package]
# Stufen: "publish" erzeugt die Programmdateien in installer/publish, "package" baut daraus das MSI.
# Der Release-Build signiert dazwischen die EXE-Dateien und danach das MSI (.github/workflows/release.yml).
param([string]$Version = "0.1.0", [ValidateSet("all", "publish", "package")][string]$Stage = "all")
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $PSScriptRoot "publish"
function Invoke-Checked { & $args[0] $args[1..($args.Count - 1)]; if ($LASTEXITCODE -ne 0) { throw "Fehlgeschlagen: $($args -join ' ')" } }

if ($Stage -in "all", "publish") {
    Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
    $common = @("-c", "Release", "-r", "win-x64", "--self-contained", "true",
                "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:Version=$Version")
    Invoke-Checked dotnet publish (Join-Path $root "src/Ylm.Agent/Ylm.Agent.csproj") @common -o (Join-Path $publish "agent")
    Invoke-Checked dotnet publish (Join-Path $root "src/Ylm.Agent.Tray/Ylm.Agent.Tray.csproj") @common -o (Join-Path $publish "tray")
}

if ($Stage -in "all", "package") {
    Invoke-Checked dotnet build (Join-Path $PSScriptRoot "Ylm.Agent.Installer.wixproj") -c Release -p:ProductVersion=$Version "-p:PublishDir=$publish\" -o (Join-Path $PSScriptRoot "out")
    Write-Host "MSI: $(Join-Path $PSScriptRoot 'out\YubikeyLifecycleManager-Agent.msi')"
}
