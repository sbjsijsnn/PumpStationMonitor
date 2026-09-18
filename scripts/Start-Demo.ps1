param([switch]$Build)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $projectRoot 'artifacts/release'
$appExe = Join-Path $releaseRoot 'App/PumpStationMonitor.App.exe'
$simulatorExe = Join-Path $releaseRoot 'Simulator/PumpStationMonitor.Simulator.exe'
if ($Build -or -not (Test-Path -LiteralPath $appExe) -or -not (Test-Path -LiteralPath $simulatorExe)) {
    & dotnet build (Join-Path $projectRoot 'PumpStationMonitor.sln') -c Release
    if ($LASTEXITCODE -ne 0) {
        throw '构建失败，请查看上方输出。'
    }
    $appBuildFolder = Join-Path $projectRoot 'src/PumpStationMonitor.App/bin/Release/net10.0-windows10.0.19041.0'
    $simulatorBuildFolder = Join-Path $projectRoot 'src/PumpStationMonitor.Simulator/bin/Release/net10.0'
    $appExe = Join-Path $appBuildFolder 'PumpStationMonitor.App.exe'
    $simulatorExe = Join-Path $simulatorBuildFolder 'PumpStationMonitor.Simulator.exe'
}
$simulatorStart = New-Object System.Diagnostics.ProcessStartInfo
$simulatorStart.FileName = $simulatorExe
$simulatorStart.Arguments = '1502'
$simulatorStart.UseShellExecute = $false
$simulatorStart.CreateNoWindow = $true
$simulatorStart.RedirectStandardInput = $true
$simulator = New-Object System.Diagnostics.Process
$simulator.StartInfo = $simulatorStart
try {
    [void]$simulator.Start()
    Start-Sleep -Milliseconds 700
    if ($simulator.HasExited) {
        throw '模拟器启动失败。请检查 1502 端口是否被占用。'
    }
    $application = Start-Process -FilePath $appExe -PassThru
    Write-Host '管理员：admin / Admin123!'
    Write-Host '操作员：operator / Operator123!'
    Write-Host '模拟命令：fault 1、fault 2、level 9.5、offline、recover；quit 退出模拟器。'
    Write-Host '应用默认连接 127.0.0.1:1502；如果改过通信设置，请在应用内调整。'
    while (-not $simulator.HasExited -and -not $application.HasExited) {
        $stationCommand = Read-Host '模拟器命令'
        $simulator.StandardInput.WriteLine($stationCommand)
        $simulator.StandardInput.Flush()
        if ($stationCommand -eq 'quit') {
            break
        }
    }
}
finally {
    if ($null -ne $simulator -and -not $simulator.HasExited) {
        $simulator.StandardInput.WriteLine('quit')
        $simulator.StandardInput.Flush()
        if (-not $simulator.WaitForExit(10000)) {
            Write-Warning '模拟器仍在关闭，请查看日志。'
        }
    }
    if ($null -ne $simulator) {
        $simulator.Dispose()
    }
}
