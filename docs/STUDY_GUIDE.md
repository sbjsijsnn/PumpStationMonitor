# 阅读与自检

## 阅读顺序

1. `src/PumpStationMonitor.Core/Models/TagDefinition.cs`：先区分设备、点位定义与一次采样值。
2. `Protocol/ModbusFrames.cs`：手算一帧 03 请求，观察寄存器大端与 CRC 小端。
3. `Protocol/IModbusClient.cs` → `ModbusClientBase.cs`：理解调用入口、异步锁、重试与状态事件。
4. `Protocol/ModbusTcpClient.cs` → `ModbusRtuClient.cs`：比较同一 PDU 的两种运输方式。
5. `src/PumpStationMonitor.Simulator/StationState.cs` → `ModbusServer.cs`：看如何生成值并回应请求。
6. `Core/Alarms/AlarmEngine.cs`：画出等待、活动、恢复与确认的关系。
7. `examples/PumpStationMonitor.ConsoleDemo/Program.cs`，最后逐条阅读 `tests/`。

第 2～4 步的路径均相对 Core；第 6 步 Core 指 `src/PumpStationMonitor.Core`。

## 模块自检

模型：点位定义与采样为什么分开？坏质量的 0 能否认为正常？0x8000 的第 15 位如何读取？为何浮点数需要配置四种字节序？

协议：MBAP 长度包含哪些字节？CRC 为什么先发低字节？读 9 个线圈为什么需要 2 字节？写入回显错误为何要拒绝？

客户端：锁为什么覆盖整次收发？取消与超时怎样区分？写超时为什么不重试？下一次请求如何恢复连接？

模拟器：保持与输入寄存器有什么不同？状态 14 的四个位代表什么？offline 与关闭监听有什么不同？哪里确保控制命令和模型更新不会发生数据竞争？

报警：延时如何被正常值打断？H=8、死区=1 时 7 是否恢复？坏质量为什么不恢复报警？确认是否意味着设备恢复？

测试：已知报文测试与往返测试有什么差别？如何制造 TCP 分包？测试为何不用固定端口？为什么串口编码测试不等于串口链路测试？

## 20 道面试题与答题要点

1. Modbus RTU 结构？站号、功能码与数据、CRC；CRC 覆盖除 CRC 本身外的整帧。
2. CRC16 原理？初值 FFFF，对每字节异或并移位，最低位为 1 时异或 A001。
3. MBAP 是什么？事务号 2 字节、协议号 2、长度 2、站号 1；随后 PDU。
4. TCP 粘包怎么处理？不把一次 Read 当成一帧；先读满头再按长度读正文。
5. 01/02 与 03/04 区别？前者读取打包位，后者读取 16 位寄存器；各自读不同区域。
6. 05/06/16 分别做什么？写单线圈、单寄存器、多个寄存器，成功应答需关联请求。
7. 为什么校验事务号？防止把旧响应或其他请求的响应当作本次结果。
8. 浮点数为什么会读错？两个寄存器的字顺序或寄存器内字节顺序可能不同。
9. 超时后写命令能直接重发吗？不能假设未执行，应先回读或由业务决定。
10. 指数退避为何有用？降低故障期间请求压力，上限防止等待无限增大。
11. 异步锁为何不用普通 lock 包裹 await？跨等待使用 SemaphoreSlim，并在 finally 释放。
12. CancellationToken 有什么价值？统一停止等待、连接、读写和重试；避免退出后后台仍运行。
13. 报警死区有什么作用？触发与恢复用不同阈值，减少边界抖动。
14. 报警延时怎样实现？记录首次越限时间，持续越限足够久才产生；中途正常或坏质量重置。
15. 确认与恢复有什么区别？确认是人已知悉，恢复是测量条件解除；共享发生事件 ID 方便追踪。
16. 跨线程如何更新 UI？App 通过 Dispatcher 切回 UI 线程，不在后台修改 ObservableCollection。
17. DI 有什么好处？宿主集中管理依赖与生命周期，便于替换客户端和测试实现。
18. Channel 为什么比 lock+List 适合历史写入？使用异步等待和有界背压连接生产消费，不需手工轮询队列。
19. SQLite 批量写入为何用事务？使用事务摊薄提交成本，并用参数避免拼接 SQL。
20. 性能指标如何可信？记录环境、采样方法、数据量和原始证据；未跑的 2 小时稳定性或 50 万条查询不能编造。

第 16～19 题可结合 Data 与 App 项目验证。


## 数据与界面阅读路线

8. `Data/schema.sql` → `StationRepository.cs` → `Seed.cs`：七表、参数 SQL、默认点表。
9. `Data/Services/HistoryWriter.cs` → `PollingService.cs` → `StationRuntime.cs`：批次、取消、分组读取、快照与报警事件。
10. `Data/Security/PasswordHasher.cs` → `SessionService.cs` → `StationFacade.cs`：登录、角色与业务规则。
11. 从 `src/PumpStationMonitor.App/App.xaml.cs` 阅读宿主、页面注册和窗口生命周期。
    登录逻辑位于 `src/PumpStationMonitor.App/ViewModels/LoginViewModel.cs`。
    主窗口位于 `src/PumpStationMonitor.App/Views/MainWindow.xaml`，只组合导航、状态栏和六个页面。
    `src/PumpStationMonitor.App/ViewModels/MainViewModel.cs` 只维护导航、连接信息和时钟。
12. `tests/PumpStationMonitor.Tests/DataTests.cs`：从真实模拟器走到 SQLite，检查权限、重启、清理和退出排空。

路径中 Data/App 指 src/PumpStationMonitor.Data 和 src/PumpStationMonitor.App。

### 六个页面的文件对应

下表路径相对 `src/PumpStationMonitor.App/`；每个页面的 `.xaml.cs` 与 `.xaml` 放在同一目录。

| 页面 | UserControl | ViewModel |
| --- | --- | --- |
| 总览 | `Views/Pages/OverviewPage.xaml` | `ViewModels/OverviewPageViewModel.cs` |
| 实时数据 | `Views/Pages/RealtimePage.xaml` | `ViewModels/RealtimePageViewModel.cs` |
| 趋势 | `Views/Pages/TrendPage.xaml` | `ViewModels/TrendPageViewModel.cs` |
| 报警 | `Views/Pages/AlarmsPage.xaml` | `ViewModels/AlarmsPageViewModel.cs` |
| 历史 | `Views/Pages/HistoryPage.xaml` | `ViewModels/HistoryPageViewModel.cs` |
| 设置 | `Views/Pages/SettingsPage.xaml` | `ViewModels/SettingsPageViewModel.cs` |

先读 `ViewModels/PageRefreshCoordinator.cs`，理解初始化、配置刷新与 250 ms 定时分发。
再读 `ViewModels/PageContext.cs` 和 `ViewModels/PageViewModel.cs`，理解共享身份、提示与命令错误处理。
`ViewModels/QueryTimeRange.cs` 保留报警历史和采样历史共用的查询时间。
趋势缓存、报警列表、历史回放和设置编辑状态分别归各页面所有，不再集中于主窗口模型。

### 控制配置与关键流程

控制映射定义在 `src/PumpStationMonitor.Core/Services/StationContracts.cs` 的 `PumpControlSettings`。
初始配置位于 `src/PumpStationMonitor.App/appsettings.json` 的 `Communication.PumpControls`，以泵号为键。
每项包含设备 ID、线圈地址、寄存器地址和启停值；默认仍为线圈 0/1、寄存器 40/41、启停值 1/0。
数据库初始化后以 `comm_settings` 中的配置为准；旧配置缺少该字段时使用相同默认映射。
`src/PumpStationMonitor.Data/Services/StationRuntime.cs` 的 `ControlAsync` 查配置后寻址，不再拼设备 ID 或计算地址。

逐段注释可从以下方法开始阅读：

- `src/PumpStationMonitor.Core/Protocol/ModbusClientBase.cs`：`ExecuteAsync` 的锁、超时与读写重试区别。
- `src/PumpStationMonitor.Core/Alarms/AlarmEngine.cs`：`Evaluate` 的质量检查、延时、死区与状态变化。
- `src/PumpStationMonitor.Data/Services/HistoryWriter.cs`：`ExecuteAsync` 的有界批次、等待复用与退出排空。
- `src/PumpStationMonitor.Data/Services/StationRuntime.cs`：`PollAsync` 的分组、解码、坏质量与快照发布。

本次只做可读性重构，不扩充测试数量。验收使用 `dotnet format`、全解决方案构建和 `dotnet test`；
测试应保持 57 项通过、0 失败、0 跳过。源代码行宽不超过 120 字符，忽略生成目录中的文件。

数据库自检：为什么 history 不因删除点位而级联删除？WAL 对查询有什么帮助？索引字段顺序为什么是 tag_id,timestamp？保留策略为什么分块删除？

采集自检：23 个点位为什么只读 6 次？稀疏地址为什么不能盲目合并？队列满时为什么等待？关闭时谁先停止？

界面自检：DispatcherTimer 回调在哪个线程？为什么过期数据显示破折号？曲线为何限制点数？保存后为什么需要刷新配置？

权限自检：按钮禁用是否足够？账号被禁用后服务如何发现？最后一个管理员为何不能降级？日志为什么不输出密码？

两个月学习安排：第 1–2 周协议与测试；第 3 周模拟器与点表；第 4 周数据库和 Channel；第 5 周报警；第 6 周 MVVM 与图表；第 7 周权限、配置和异常；第 8 周独立演示、故障排查与面试复述。每周自己修改一个小功能并补充测试。
