$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $projectRoot 'artifacts/release'
& dotnet publish (Join-Path $projectRoot 'src/PumpStationMonitor.App') -c Release -r win-x64 --self-contained true -o (Join-Path $releaseRoot 'App')
if ($LASTEXITCODE -ne 0) { throw '应用发布失败' }
& dotnet publish (Join-Path $projectRoot 'src/PumpStationMonitor.Simulator') -c Release -r win-x64 --self-contained true -o (Join-Path $releaseRoot 'Simulator')
if ($LASTEXITCODE -ne 0) { throw '模拟器发布失败' }
Write-Host "发布目录：$releaseRoot"
