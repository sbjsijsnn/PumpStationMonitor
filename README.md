# 泵站设备监控上位机

个人工业监控项目，模拟两台水泵、集水池与出水管路。当前完成 **M1：Core、TCP 模拟器、控制台演示与测试**。WPF 界面及数据库尚未进入实现阶段。

运行与学习入口：[docs/README.md](docs/README.md)。里程碑说明：[M1](docs/notes/M1.md)。

```powershell
dotnet build -c Release
dotnet test -c Release
dotnet run --project src/PumpStationMonitor.Simulator -c Release
```

另开终端，进入本仓库：

```powershell
dotnet run --project examples/PumpStationMonitor.ConsoleDemo -c Release -- 1502 --debug
```

本机 SDK 为 10.0.400，现有项目采用纯 .NET 10。后续 WPF 使用 net10.0-windows。当前以命令行构建验收，未验证 Visual Studio 2022 对该 SDK 的支持；不要将解决方案格式可打开等同于 IDE 可编译。
