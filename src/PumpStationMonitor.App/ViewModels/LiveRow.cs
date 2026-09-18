using CommunityToolkit.Mvvm.ComponentModel;
using PumpStationMonitor.Core.Services;

namespace PumpStationMonitor.App.ViewModels;

// 承载实时数据表格中一行的展示状态。
// 保留点位定义并独立通知数值变化，避免每轮重建整张表。
// 质量与更新时间分别显示，便于识别过期采样。
public partial class LiveRow(TagRow tag) : ObservableObject
{
    public TagRow Tag { get; } = tag;
    public string Id => Tag.Id;
    public string Name => Tag.Name;
    public string Device => Tag.DeviceId switch
    {
        "pump1" => "1# 水泵",
        "pump2" => "2# 水泵",
        "sump" => "集水池",
        _ => "出水管路"
    };
    public string Unit => Tag.Unit;
    public string Address => $"{Tag.Function:D2} / {Tag.Address}" + (Tag.Bit.HasValue ? $" / bit{Tag.Bit}" : "");

    [ObservableProperty]
    private string valueText = "—";
    [ObservableProperty]
    private string quality = "等待";
    [ObservableProperty]
    private string updated = "—";
}

// 承载总览页面的一张设备工况卡片。
// 名称等固定信息与状态等可变信息分开，降低刷新对界面的影响。
// 使用属性通知同步颜色、数值与进度条。
public partial class DeviceCard(string id, string name, string subtitle) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Subtitle { get; } = subtitle;

    [ObservableProperty]
    private string status = "等待数据";
    [ObservableProperty]
    private string color = "#7D8E9A";
    [ObservableProperty]
    private string primary = "—";
    [ObservableProperty]
    private string primaryUnit = "";
    [ObservableProperty]
    private string secondary = "";
    [ObservableProperty]
    private double gauge;
}
