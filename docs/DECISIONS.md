# 技术决策

| 选择 | 原因 | 备选与代价 |
|---|---|---|
| .NET 10 / Windows 10 2004 | 本机只有 10.0.400；SkiaSharp WPF 的现代目标要求 windows10.0.19041 | .NET 8 需安装 SDK；放弃默认 windows7.0 避免回退旧 Framework 包 |
| Core / Data / App / Simulator 分层 | 协议、报警无 WPF 依赖，客户端可独立测试 | 单 WPF 工程更短但业务与 UI 耦合 |
| CommunityToolkit.Mvvm 8.4.2 | ObservableProperty / RelayCommand 生成绑定与命令 | 手写通知重复多，源生成需要理解生成规则 |
| HandyControl 3.5.1 | 免费 MIT，提供 WPF 主题资源 | 自绘全部控件维护成本高，局部按钮与进度条按场景覆盖 |
| LiveCharts2 2.0.5 | 实时曲线、历史回放，MIT | 手绘曲线需自行实现坐标和交互；Skia 依赖使发布包较大 |
| Hosting 10.0.12 / DI / Configuration | 生命周期、后台服务、配置集中管理 | 手工启动需自行处理退出顺序 |
| Serilog Hosting 10.0.0 / File 7.0.0 | 按天日志，协议 Debug、异常 Error、控制审计 | 输出较多，保留 14 个日文件；生产可调整级别 |
| 自研 Modbus TCP/RTU | 明确解释 PDU、CRC、MBAP 与粘包 | 需要自己验证边界，不使用外部 Modbus 客户端库 |
| 请求串行、读重试、写不重发 | 防止交叉收发及重复控制；读失败指数退避 | TCP 并行吞吐受限，默认四设备足够 |
| Dapper 2.1.79 / Microsoft.Data.Sqlite 10.0.12 | 实体映射、参数 SQL、事务与 WAL | 不提供完整 ORM 迁移，schema.sql 负责首次建库 |
| Channel 有界 20,000 条 | 异步背压、200 条/1 秒批次、正常停止排空 | 数据库长期故障会阻塞采集，明确记录错误 |
| 报警引擎采样时间驱动 | 死区与延时可确定性测试 | 必须串行、时间有序；系统时钟回拨会报错而不伪造顺序 |
| 配置变更与采集共用运行时门锁 | 修改规则之前持久化待写报警，避免竞争丢失活动状态 | 保存可能等待正在执行的通信超时 |
| PBKDF2-SHA256 | 系统加密 API、随机盐、固定时间比较 | 本地演示账号需用户自行更改，不是域账号体系 |
| 查询与显示上限 | 控制 UI 内存与响应时间 | 每次 10,000 行，范围大时需要缩小查询；曲线抽样 |

依赖许可证通过已下载 NuGet 包的 nuspec/LICENSE 检查：HandyControl、LiveCharts、CommunityToolkit、Microsoft 库为 MIT；Dapper、Serilog 为 Apache-2.0。未引用付费控件。项目 NuGet.Config 只启用 nuget.org；packages.lock.json 固定依赖图。
