<#
.SYNOPSIS
  Builds the FanControl MCP plugin and installs it into FanControl's Plugins folder.

.DESCRIPTION
  - builds src/FanControl.MCP in Release
  - copies FanControl.MCP.dll into <FanControlDir>\Plugins (asks for elevation if needed)
  - restarts FanControl (desktop app or Windows service, whichever was running) unless -NoRestart

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
param(
    [string]$FanControlDir = "C:\Program Files (x86)\FanControl",
    [switch]$NoRestart,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$plugins = Join-Path $FanControlDir "Plugins"
$target = Join-Path $plugins "FanControl.MCP.dll"

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Path (Join-Path $FanControlDir "FanControl.exe"))) {
    throw "FanControl not found in '$FanControlDir'. Pass -FanControlDir <path>."
}

$dll = Join-Path $root "src\FanControl.MCP\bin\Release\net10.0-windows10.0.22621.0\FanControl.MCP.dll"
if (-not $Uninstall) {
    Write-Host "Building plugin..."
    & dotnet build (Join-Path $root "src\FanControl.MCP") -c Release -nologo -v q "-p:FanControlDir=$FanControlDir"
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

if (-not (Test-Admin)) {
    Write-Host "Administrator rights are required to write to '$plugins'. Re-launching elevated..."
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-FanControlDir", "`"$FanControlDir`"")
    if ($NoRestart) { $argList += "-NoRestart" }
    if ($Uninstall) { $argList += "-Uninstall" }
    $p = Start-Process powershell -Verb RunAs -ArgumentList $argList -Wait -PassThru
    exit $p.ExitCode
}

# --- elevated from here on ---------------------------------------------------
$service = Get-Service "FanControl.Service" -ErrorAction SilentlyContinue
$serviceWasRunning = $service -and $service.Status -eq "Running"
$app = Get-Process FanControl -ErrorAction SilentlyContinue | Select-Object -First 1
$appPath = if ($app) { $app.Path } else { Join-Path $FanControlDir "FanControl.exe" }

if (-not $NoRestart) {
    if ($app) { Write-Host "Stopping FanControl..."; $app | Stop-Process -Force; $app.WaitForExit(10000) | Out-Null }
    if ($serviceWasRunning) { Write-Host "Stopping FanControl.Service..."; Stop-Service "FanControl.Service" -Force }
}

New-Item -ItemType Directory -Force $plugins | Out-Null
if ($Uninstall) {
    Remove-Item $target -ErrorAction SilentlyContinue
    Write-Host "Removed $target (settings file FanControl.MCP.json was kept)."
} else {
    Copy-Item $dll $target -Force
    Write-Host "Installed $target"
}

if (-not $NoRestart) {
    if ($serviceWasRunning) { Write-Host "Starting FanControl.Service..."; Start-Service "FanControl.Service" }
    if ($app -or -not $serviceWasRunning) { Write-Host "Starting FanControl..."; Start-Process $appPath }
}

if (-not $Uninstall) {
    Write-Host ""
    Write-Host "MCP endpoint (after FanControl has started): http://127.0.0.1:48620/mcp"
    Write-Host "Settings: $(Join-Path $plugins 'FanControl.MCP.json')"
}
