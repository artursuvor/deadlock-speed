param(
    [string]$DeadlockDir = "C:\Program Files (x86)\Steam\steamapps\common\Deadlock",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8NoBom
[Console]::OutputEncoding = $utf8NoBom
$OutputEncoding = $utf8NoBom

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $projectDir "DeadlockAverageSpeed.csproj"
$managedDir = Join-Path $DeadlockDir "game\bin\win64\managed"
$pluginDir = Join-Path $DeadlockDir "game\bin\win64\managed\plugins"
$sdkDir = Join-Path $projectDir "..\vendor\deadworks"

if (-not (Test-Path $managedDir)) {
    throw "Deadworks managed folder was not found: $managedDir. Install/extract Deadworks into the Deadlock folder first."
}

if (-not (Test-Path $pluginDir)) {
    New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null
}

$buildArgs = @("build", $projectPath, "-c", $Configuration, "/p:DeadlockDir=$DeadlockDir")
if (Test-Path (Join-Path $sdkDir "game\bin\win64\managed\DeadworksManaged.Api.dll")) {
    $buildArgs += "/p:DeadworksSdkDir=$sdkDir"
}

dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build exited with code $LASTEXITCODE."
}

$outputDir = Join-Path $projectDir "bin\$Configuration\net10.0"
Copy-Item -Force (Join-Path $outputDir "DeadlockAverageSpeed.dll") $pluginDir

$pdbPath = Join-Path $outputDir "DeadlockAverageSpeed.pdb"
if (Test-Path $pdbPath) {
    Copy-Item -Force $pdbPath $pluginDir
}

Write-Host "Installed DeadlockAverageSpeed to $pluginDir"
