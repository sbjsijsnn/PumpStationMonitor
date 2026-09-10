param([int]$Seconds = 600)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location $projectRoot
try {
    & dotnet restore --locked-mode
    if ($LASTEXITCODE -ne 0) { throw '依赖还原失败' }
    & dotnet build -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw '构建失败' }
    & dotnet test -c Release --no-build --logger 'trx;LogFileName=verification.trx'
    if ($LASTEXITCODE -ne 0) { throw '测试失败' }
    $evidenceFolder = Join-Path $projectRoot ('artifacts/verification-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    & dotnet run --project examples/PumpStationMonitor.Host -c Release --no-build -- --seconds $Seconds --output $evidenceFolder
    if ($LASTEXITCODE -ne 0) { throw '连续采集验证失败' }
    Write-Host "验证记录：$evidenceFolder"
}
finally { Pop-Location }
