param(
    [switch]$KeepGame,
    [int]$GraceSeconds = 5
)

$ErrorActionPreference = "Stop"

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8NoBom
[Console]::OutputEncoding = $utf8NoBom
$OutputEncoding = $utf8NoBom

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$sessionFile = Join-Path $root ".speed-session.json"
$handledProcessIds = @{}

function Stop-SpeedSessionProcess {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if ($handledProcessIds.ContainsKey($Process.Id)) {
        return
    }

    $handledProcessIds[$Process.Id] = $true

    try {
        if ($Process.HasExited) {
            return
        }

        Write-Host "Stopping $Label (pid $($Process.Id))..."
        $closed = $false
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            $closed = $Process.CloseMainWindow()
        }

        if ($closed -and $GraceSeconds -gt 0) {
            $Process.WaitForExit($GraceSeconds * 1000) | Out-Null
            $Process.Refresh()
        }

        if (-not $Process.HasExited) {
            Stop-Process -Id $Process.Id -Force -ErrorAction Stop
        }
    }
    catch [System.InvalidOperationException] {
        return
    }
    catch {
        Write-Warning "Could not stop $Label (pid $($Process.Id)): $($_.Exception.Message)"
    }
}

function Stop-ProcessById {
    param(
        [int]$Id,
        [string]$ExpectedName,
        [string]$Label
    )

    $process = Get-Process -Id $Id -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        return
    }

    if ($process.ProcessName -ne $ExpectedName) {
        return
    }

    Stop-SpeedSessionProcess -Process $process -Label $Label
}

function Stop-ProcessByName {
    param(
        [string]$Name,
        [string]$Label
    )

    foreach ($process in @(Get-Process -Name $Name -ErrorAction SilentlyContinue)) {
        Stop-SpeedSessionProcess -Process $process -Label $Label
    }
}

if (Test-Path $sessionFile) {
    try {
        $session = Get-Content -Raw -Path $sessionFile | ConvertFrom-Json
        if ($session.DeadworksPid) {
            Stop-ProcessById -Id ([int]$session.DeadworksPid) -ExpectedName "deadworks" -Label "Deadworks server"
        }

        if ($session.OverlayPid) {
            Stop-ProcessById -Id ([int]$session.OverlayPid) -ExpectedName "DeadlockAverageSpeedOverlay" -Label "speed overlay"
        }
    }
    catch {
        Write-Warning "Could not read session file: $($_.Exception.Message)"
    }
}

Stop-ProcessByName -Name "DeadlockAverageSpeedOverlay" -Label "speed overlay"
Stop-ProcessByName -Name "deadworks" -Label "Deadworks server"

if ($KeepGame) {
    Write-Host "Keeping Deadlock running."
}
else {
    Stop-ProcessByName -Name "deadlock" -Label "Deadlock client"
}

if (Test-Path $sessionFile) {
    Remove-Item -Force -Path $sessionFile -ErrorAction SilentlyContinue
}

Write-Host "Speed session stopped."
