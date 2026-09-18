$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $projectRoot 'artifacts/release'
$appProject = Join-Path $projectRoot 'src/PumpStationMonitor.App'
$appOutput = Join-Path $releaseRoot 'App'
& dotnet publish $appProject -c Release -r win-x64 --self-contained true -o $appOutput
if ($LASTEXITCODE -ne 0) {
    throw '应用发布失败'
}
$simulatorProject = Join-Path $projectRoot 'src/PumpStationMonitor.Simulator'
$simulatorOutput = Join-Path $releaseRoot 'Simulator'
& dotnet publish $simulatorProject -c Release -r win-x64 --self-contained true -o $simulatorOutput
if ($LASTEXITCODE -ne 0) {
    throw '模拟器发布失败'
}
Write-Host "发布目录：$releaseRoot"
