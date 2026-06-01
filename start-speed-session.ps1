param(
    [string]$DeadlockDir = "C:\Program Files (x86)\Steam\steamapps\common\Deadlock",
    [string]$SteamExe = "C:\Program Files (x86)\Steam\steam.exe",
    [int]$AppId = 1422450,
    [switch]$NoBuild,
    [switch]$NoGame,
    [switch]$ShowServer
)

$ErrorActionPreference = "Stop"

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8NoBom
[Console]::OutputEncoding = $utf8NoBom
$OutputEncoding = $utf8NoBom

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pluginDir = Join-Path $root "DeadlockAverageSpeed"
$overlayExe = Join-Path $root "DeadlockAverageSpeedOverlay\bin\Release\net10.0-windows\DeadlockAverageSpeedOverlay.exe"
$overlayProject = Join-Path $root "DeadlockAverageSpeedOverlay\DeadlockAverageSpeedOverlay.csproj"
$installScript = Join-Path $pluginDir "install-plugin.ps1"
$deadworksExe = Join-Path $DeadlockDir "game\bin\win64\deadworks.exe"
$deadworksCwd = Split-Path -Parent $deadworksExe
$sessionFile = Join-Path $root ".speed-session.json"

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [string[]]$ArgumentList = @()
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE."
    }
}

if (-not (Test-Path $deadworksExe)) {
    throw "deadworks.exe was not found: $deadworksExe"
}

if (-not $NoBuild) {
    Write-Host "[1/5] Building and installing speed plugin..."
    Invoke-NativeCommand -FilePath "powershell.exe" -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $installScript,
        "-DeadlockDir", $DeadlockDir
    )

    Write-Host "[2/5] Building overlay..."
    Invoke-NativeCommand -FilePath "dotnet" -ArgumentList @("build", $overlayProject, "-c", "Release")
}
else {
    Write-Host "[1/5] Skipping build/install."
    Write-Host "[2/5] Skipping overlay build."
}

Write-Host "[3/5] Restarting Deadworks server..."
Get-Process deadworks -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$serverWindowStyle = if ($ShowServer) { "Normal" } else { "Hidden" }
$deadworksProcess = Start-Process -FilePath $deadworksExe -WorkingDirectory $deadworksCwd -WindowStyle $serverWindowStyle -PassThru
Start-Sleep -Seconds 2

Write-Host "[4/5] Restarting speed overlay..."
Get-Process DeadlockAverageSpeedOverlay -ErrorAction SilentlyContinue | Stop-Process -Force
$overlayProcess = Start-Process -FilePath $overlayExe -WorkingDirectory (Split-Path -Parent $overlayExe) -PassThru

$sessionState = [ordered]@{
    StartedAt = (Get-Date).ToString("o")
    DeadworksPid = $deadworksProcess.Id
    OverlayPid = $overlayProcess.Id
    DeadlockProcessName = "deadlock"
}
$sessionState | ConvertTo-Json | Set-Content -Path $sessionFile -Encoding UTF8

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
Write-Host ""
Write-Host "To close Deadworks, the overlay, and Deadlock, run:"
Write-Host "stop-speed-session.cmd"
Write-Host "Use stop-speed-session.cmd -KeepGame to leave Deadlock running."
