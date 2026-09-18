using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using PumpStationMonitor.Core.Models;
using PumpStationMonitor.Core.Services;
using SkiaSharp;

namespace PumpStationMonitor.App.ViewModels;

// 管理点位选择、时间窗口和最近一小时的趋势缓存。
// 缓存随采集更新而不是页面切换更新，保证切回页面时仍有曲线。
// 显示时抽样控制图表点数，坏质量保留为空点。
public partial class TrendPageViewModel : PageViewModel
{
    private readonly Dictionary<string, Queue<LiveValue>> trendBuffer = [];
    private readonly ObservableCollection<ObservablePoint> trendValues = [];
    public int[] Spans { get; } = [1, 5, 15, 60];

    [ObservableProperty]
    private List<TagRow> tags = [];
    [ObservableProperty]
    private TagRow? trendTag;
    [ObservableProperty]
    private int spanMinutes = 5;
    public ISeries[] TrendSeries
    {
        get;
    }
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

    partial void OnTrendTagChanged(TagRow? value) => RenderTrend();
    partial void OnSpanMinutesChanged(int value) => RenderTrend();
    private void RenderTrend()
    {
        trendValues.Clear();
        if (TrendTag is null || !trendBuffer.TryGetValue(TrendTag.Id, out var buffer))
        {
            return;
        }

        var points = buffer.Where(v => v.Timestamp >= DateTimeOffset.UtcNow.AddMinutes(-SpanMinutes)).ToArray();
        int stride = Math.Max(1, (int)Math.Ceiling(points.Length / 1500.0));
        for (int i = 0; i < points.Length; i += stride)
        {
            var p = points[i];
            trendValues.Add(
                new(
                    p.Timestamp.ToUnixTimeMilliseconds(),
                    p.Quality == DataQuality.Good ? p.Value : null
                )
            );
        }
    }

    public TrendPageViewModel(PageContext context) : base(context)
    {
        TrendSeries = [
            new LineSeries<ObservablePoint>
            {
                Values = trendValues,
                Name = "实时值",
                GeometrySize = 0,
                Fill = null,
                Stroke = new SolidColorPaint(SKColor.Parse("#147D92"), 3),
                LineSmoothness = 0
            }
        ];
    }

    public void Configure(List<TagRow> availableTags)
    {
        string selected = TrendTag?.Id ?? "level";
        Tags = availableTags;
        TrendTag = Tags.FirstOrDefault(t => t.Id == selected);
        var deletedIds = trendBuffer.Keys.Where(id => Tags.All(t => t.Id != id)).ToArray();
        foreach (string id in deletedIds)
        {
            trendBuffer.Remove(id);
        }
    }

    public void Refresh(StationSnapshot snapshot)
    {
        foreach (var value in snapshot.Values.Where(v => Tags.Any(t => t.Enabled && t.Id == v.Tag.Id)))
        {
            if (!trendBuffer.TryGetValue(value.Tag.Id, out var buffer))
            {
                buffer = new Queue<LiveValue>();
                trendBuffer[value.Tag.Id] = buffer;
            }

            buffer.Enqueue(value);
            while (buffer.Count > 36000
                    || (buffer.Count > 0 && value.Timestamp - buffer.Peek().Timestamp > TimeSpan.FromHours(1)))
            {
                buffer.Dequeue();
            }
        }

        RenderTrend();
    }
}
