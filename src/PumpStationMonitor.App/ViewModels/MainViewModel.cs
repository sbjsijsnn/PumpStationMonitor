using CommunityToolkit.Mvvm.ComponentModel;
using PumpStationMonitor.Core.Protocol;
using PumpStationMonitor.Core.Services;

namespace PumpStationMonitor.App.ViewModels;

// 提供主窗口的导航索引、连接状态和时钟。
// 页面业务交给各自的视图模型，主窗口只维护公共展示信息。
// 由刷新协调器传入快照，导航模型不直接管理采集或存储。
public partial class MainViewModel : ObservableObject
{
    public string[] Navigation { get; } = ["站点总览", "实时数据", "运行趋势", "报警中心", "历史查询", "系统设置"];

    [ObservableProperty]
    private int page;
    [ObservableProperty]
    private string connection = "连接中";
    [ObservableProperty]
    private string connectionColor = "#C38A24";
    [ObservableProperty]
    private string pollLabel = "等待采集";
    [ObservableProperty]
    private string clock = "";
    public void Refresh(StationSnapshot snapshot, int intervalMs)
    {
        Clock = DateTime.Now.ToString("yyyy年MM月dd日  HH:mm:ss");
        Connection = snapshot.Connection switch
        {
            ConnectionState.Connected => "已连接",
            ConnectionState.Reconnecting => "重连中",
            ConnectionState.Connecting => "连接中",
            _ => "已断开"
        };
        bool connected = snapshot.Connection == ConnectionState.Connected;
        ConnectionColor = connected ? "#19866C" : "#C06932";
        string duration = $"本轮 {snapshot.PollMilliseconds:F1} ms";
        PollLabel = $"采集周期 {intervalMs} ms  /  {duration}  /  {snapshot.PollCount:N0} 轮";
    }
}
