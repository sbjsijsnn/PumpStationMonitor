using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PumpStationMonitor.Core.Models;
using PumpStationMonitor.Core.Services;

namespace PumpStationMonitor.App.ViewModels;

// 将采集快照转换为设备卡片，提供泵组启停命令。
// 把工况展示与控制入口留在总览页面，不让导航模型承担设备业务。
// 报警摘要和连接状态直接引用已有模型，避免维护两份状态。
public partial class OverviewPageViewModel : PageViewModel
{
    public ObservableCollection<DeviceCard> Devices
    {
        get;
    } = [
        new("pump1", "1# 水泵", "主用机组"),
        new("pump2", "2# 水泵", "备用机组"),
        new("sump", "集水池", "来水与液位"),
        new("outlet", "出水管路", "压力与输送")
    ];
    public string[] ControlModes { get; } = ["Coil", "Register"];

    [ObservableProperty]
    private string controlMode = "Coil";
    private void UpdateCards(StationSnapshot snapshot, bool stale)
    {
        var data = snapshot.Values.ToDictionary(v => v.Tag.Id);
        foreach (var card in Devices)
        {
            var samples = snapshot.Values.Where(value => value.Tag.DeviceId == card.Id).ToArray();
            int maximumAge = Math.Max(3000, Context.Communication.PollIntervalMs * 3);
            bool good = !stale && samples.Length > 0 && samples.All(value => IsFresh(value, maximumAge));
            double V(string id) => data.GetValueOrDefault(id)?.Value ?? 0;
            if (!good)
            {
                card.Status = "数据不可用";
                card.Color = "#7D8E9A";
                card.Primary = "—";
                card.Secondary = "检查通信连接";
                card.Gauge = 0;
                continue;
            }

            if (card.Id.StartsWith("pump", StringComparison.Ordinal))
            {
                bool fault = V(card.Id + "_fault") != 0;
                bool run = V(card.Id + "_run") != 0;
                card.Status = fault ? "故障" : run ? "运行中" : "待机";
                card.Color = fault ? "#CA4C41" : run ? "#19866C" : "#7D8E9A";
                card.Primary = V(card.Id + "_frequency").ToString("F1");
                card.PrimaryUnit = "Hz";
                card.Secondary = $"电流 {V(card.Id + "_current"):F1} A   累计 {V(card.Id + "_hours"):F0} h";
                card.Gauge = V(card.Id + "_frequency") / 60 * 100;
            }
            else if (card.Id == "sump")
            {
                card.Status = V("high_switch") != 0 ? "高液位" : V("low_switch") != 0 ? "低液位" : "液位正常";
                card.Color = card.Status == "液位正常" ? "#19866C" : "#CA4C41";
                card.Primary = V("level").ToString("F2");
                card.PrimaryUnit = "m";
                card.Secondary = $"进水流量 {V("inflow"):F1} m³/h";
                card.Gauge = V("level") / 10 * 100;
            }
            else
            {
                card.Status = "监测中";
                card.Color = "#19866C";
                card.Primary = V("pressure").ToString("F3");
                card.PrimaryUnit = "MPa";
                card.Secondary = $"流量 {V("outflow"):F1} m³/h   阀门 {V("valve"):F0}%";
                card.Gauge = V("valve");
            }
        }
    }

    private static bool IsFresh(LiveValue value, int maximumAge)
    {
        double age = (DateTimeOffset.UtcNow - value.Timestamp).TotalMilliseconds;
        return value.Quality == DataQuality.Good && age <= maximumAge;
    }

    [RelayCommand]
    private async Task ControlAsync(string parameter) => await RunAsync(
        async () =>
        {
            var parts = parameter.Split(':');
            int pump = int.Parse(parts[0], CultureInfo.InvariantCulture);
            bool run = parts[1] == "start";
            await facade.ControlAsync(pump, run, ControlMode);
        },
        "控制命令已应答，状态将在下一采集周期更新"
    );
    public AlarmsPageViewModel Alarms
    {
        get;
    }
    public MainViewModel Shell
    {
        get;
    }

    public OverviewPageViewModel(
        PageContext context,
        AlarmsPageViewModel alarms,
        MainViewModel shell
    ) : base(context)
    {
        Alarms = alarms;
        Shell = shell;
    }

    public void Refresh(StationSnapshot snapshot, bool stale) => UpdateCards(snapshot, stale);
}
