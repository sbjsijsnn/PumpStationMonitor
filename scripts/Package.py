from pathlib import Path
import zipfile

root = Path(__file__).resolve().parents[1]
release = root / "artifacts/release"
launcher = r"""@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Demo.ps1"
if errorlevel 1 pause
"""
script = (root / "scripts/Start-Demo.ps1").read_text(encoding="utf-8-sig")
a = script.index("$projectRoot =")
b = script.index("$simulatorStart =")
script = (
    script[:a]
    + "$releaseRoot = $PSScriptRoot\n"
    "$appExe = Join-Path $releaseRoot 'App/PumpStationMonitor.App.exe'\n"
    "$simulatorExe = Join-Path $releaseRoot 'Simulator/PumpStationMonitor.Simulator.exe'\n"
    + script[b:]
)
output = root / "artifacts/PumpStationMonitor-Windows-x64.zip"
with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED, compresslevel=5) as archive:
    archive.writestr("Start-Demo.cmd", launcher)
    archive.writestr("Start-Demo.ps1", script.encode("utf-8-sig"))
    archive.writestr(
        "使用说明.txt",
        (
            "解压整个文件夹后双击 Start-Demo.cmd。管理员 admin / Admin123!；操作员 operator / Operator123!。"
            "命令窗口可输入 fault 1、level 9.5、offline、recover。关闭界面后输入 quit 结束模拟器。"
            "最低 Windows 10 2004，x64，无需安装 .NET。完整手册见 docs/README.md。"
        ).encode("utf-8-sig"),
    )
    for file in release.rglob("*"):
        if file.is_file():
            archive.write(file, file.relative_to(release))
    for file in (root / "docs").rglob("*"):
        if file.is_file():
            archive.write(file, file.relative_to(root))
print(str(output))
print(f"{output.stat().st_size/1048576:.1f} MiB")
