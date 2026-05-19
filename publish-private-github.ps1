param(
    [string]$RepoName = "deadlock-average-speed"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI is not installed. Install it with: winget install --id GitHub.cli -e"
}

gh auth status

$remote = git remote get-url origin 2>$null
if ($LASTEXITCODE -eq 0 -and $remote) {
    git push -u origin main
    exit
}

gh repo create $RepoName --private --source . --remote origin --push
