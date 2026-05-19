param(
    [string]$DeadlockDir = "C:\Program Files (x86)\Steam\steamapps\common\Deadlock",
    [string]$SteamExe = "C:\Program Files (x86)\Steam\steam.exe",
    [int]$AppId = 1422450,
    [switch]$NoBuild,
    [switch]$NoGame,
    [switch]$ShowServer
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pluginDir = Join-Path $root "DeadlockAverageSpeed"
$overlayExe = Join-Path $root "DeadlockAverageSpeedOverlay\bin\Release\net10.0-windows\DeadlockAverageSpeedOverlay.exe"
$overlayProject = Join-Path $root "DeadlockAverageSpeedOverlay\DeadlockAverageSpeedOverlay.csproj"
$installScript = Join-Path $pluginDir "install-plugin.ps1"
$deadworksExe = Join-Path $DeadlockDir "game\bin\win64\deadworks.exe"
$deadworksCwd = Split-Path -Parent $deadworksExe

if (-not (Test-Path $deadworksExe)) {
    throw "deadworks.exe was not found: $deadworksExe"
}

if (-not $NoBuild) {
    Write-Host "[1/5] Building and installing speed plugin..."
    & powershell -NoProfile -ExecutionPolicy Bypass -File $installScript -DeadlockDir $DeadlockDir

    Write-Host "[2/5] Building overlay..."
    dotnet build $overlayProject -c Release | Write-Host
}
else {
    Write-Host "[1/5] Skipping build/install."
    Write-Host "[2/5] Skipping overlay build."
}

Write-Host "[3/5] Restarting Deadworks server..."
Get-Process deadworks -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$serverWindowStyle = if ($ShowServer) { "Normal" } else { "Hidden" }
Start-Process -FilePath $deadworksExe -WorkingDirectory $deadworksCwd -WindowStyle $serverWindowStyle
Start-Sleep -Seconds 2

Write-Host "[4/5] Restarting speed overlay..."
Get-Process DeadlockAverageSpeedOverlay -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process -FilePath $overlayExe -WorkingDirectory (Split-Path -Parent $overlayExe)

if (-not $NoGame) {
    if (-not (Test-Path $SteamExe)) {
        throw "steam.exe was not found: $SteamExe"
    }

    Write-Host "[5/5] Starting Deadlock with +connect localhost:27067..."
    Start-Process -FilePath $SteamExe -ArgumentList @("-applaunch", "$AppId", "+connect", "localhost:27067")
}
else {
    Write-Host "[5/5] Skipping Deadlock launch."
}

Write-Host ""
Write-Host "Done. If Deadlock does not auto-connect, open the in-game console and run:"
Write-Host "connect localhost:27067"
