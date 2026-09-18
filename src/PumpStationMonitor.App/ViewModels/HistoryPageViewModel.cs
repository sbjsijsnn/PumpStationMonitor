using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using PumpStationMonitor.Core.Services;
using SkiaSharp;

namespace PumpStationMonitor.App.ViewModels;

// 处理历史采样查询、曲线绘制和逐步回放。
// 把回放位置与数据列表放在一起，使播放和手动拖动使用相同绘制逻辑。
// 图表显示时抽样，原始查询结果仍完整保留在表格中。
public partial class HistoryPageViewModel : PageViewModel
{
    [ObservableProperty]
    private List<DeviceRow> deviceOptions = [];
    [ObservableProperty]
    private List<TagRow> tags = [];
    [ObservableProperty]
    private DeviceRow? historyDevice;
    [ObservableProperty]
    private TagRow? historyTag;
    [ObservableProperty]
    private List<HistoryRow> historyRows = [];
    [ObservableProperty]
    private int replayPosition;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ReplayButton))]
    private bool replayRunning;
    public string ReplayButton => ReplayRunning ? "暂停回放" : "播放历史";
    public string ReplayTime => ReplayPosition > 0 && ReplayPosition <= HistoryRows.Count
        ? HistoryRows[ReplayPosition - 1].TimeText
        : "尚未回放";

    [ObservableProperty]
    private string querySummary = "选择点位和时间段后查询";
    [ObservableProperty]
    private ISeries[] historySeries = [];
    public Axis[] TimeAxes
    {
        get;
    } = [
        new()
        {
            MinStep = 1000,
            Labeler = value => DateTimeOffset.FromUnixTimeMilliseconds((long)value).LocalDateTime.ToString("HH:mm:ss"),
            LabelsPaint = new SolidColorPaint(SKColor.Parse("#677D8A")),
            TextSize = 11
        }
    ];
    public Axis[] ValueAxes
    {
        get;
    } = [
        new()
        {
            LabelsPaint = new SolidColorPaint(SKColor.Parse("#677D8A")),
            TextSize = 11
        }
    ];

    [RelayCommand]
    private async Task QueryHistoryAsync() => await RunAsync(
        async () =>
        {
            var (from, to) = Range();
            HistoryRows = await repository.QueryHistoryAsync(HistoryDevice?.Id, HistoryTag?.Id, from, to);
            ReplayRunning = false;
            ReplayPosition = HistoryRows.Count;
            RenderHistory();
            QuerySummary = $"查询到 {HistoryRows.Count:N0} 条记录"
                + (HistoryRows.Count == 10000 ? "（达到 10,000 条上限，请缩小时间段）" : "");
        },
        "历史查询完成"
    );
    partial void OnReplayPositionChanged(int value)
    {
        RenderHistory();
        OnPropertyChanged(nameof(ReplayTime));
    }

    private void RenderHistory()
    {
        int count = Math.Clamp(ReplayPosition, 0, HistoryRows.Count);
        int stride = Math.Max(1, (int)Math.Ceiling(count / 2000.0));
        var visibleRows = HistoryRows.Take(count).Where((_, index) => index % stride == 0);
        var points = visibleRows.Select(row =>
            {
                double? value = row.Quality == "Good" ? row.Value : null;
                return new ObservablePoint(row.Timestamp, value);
            }).ToArray();
        var series = new LineSeries<ObservablePoint>
        {
            Name = HistoryTag?.Name ?? "历史值",
            Values = points,
            GeometrySize = 0,
            Fill = null,
            Stroke = new SolidColorPaint(SKColor.Parse("#147D92"), 2),
            LineSmoothness = 0
        };
        HistorySeries = [series];
    }

    [RelayCommand]
    private void ToggleReplay()
    {
        if (HistoryRows.Count == 0)
        {
            Status = "请先查询历史记录";
            return;
        }

        if (ReplayPosition >= HistoryRows.Count)
        {
            ReplayPosition = 0;
        }

        ReplayRunning = !ReplayRunning;
    }

    public QueryTimeRange TimeRange => Context.TimeRange;

    private (long From, long To) Range() => TimeRange.Parse();
    public HistoryPageViewModel(PageContext context) : base(context)
    {
    }

    public void Configure(List<TagRow> availableTags, List<DeviceRow> devices)
    {
        Tags = availableTags;
        DeviceOptions = devices;
        HistoryTag ??= Tags.FirstOrDefault(t => t.Id == "level");
        HistoryDevice ??= DeviceOptions[0];
    }

    public void AdvanceReplay()
    {
        if (!ReplayRunning)
        {
            return;
        }

        int step = Math.Max(1, HistoryRows.Count / 200);
        ReplayPosition = Math.Min(HistoryRows.Count, ReplayPosition + step);
        if (ReplayPosition >= HistoryRows.Count)
        {
            ReplayRunning = false;
        }
    }
}
