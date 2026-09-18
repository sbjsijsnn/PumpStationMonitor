using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using PumpStationMonitor.Core.Services;
using PumpStationMonitor.Data.Services;

namespace PumpStationMonitor.App.ViewModels;

// 协调六个页面的初始化、配置刷新和定时快照分发。
// 保留统一的刷新节奏，隐藏页面的趋势缓存和回放也能继续推进。
// 用重入标记避免异步刷新重叠，并在关闭时解除计时器与事件。
public sealed class PageRefreshCoordinator : IDisposable
{
    private readonly PageContext context;
    private readonly StationRuntime runtime;
    private readonly MainViewModel shell;
    private readonly OverviewPageViewModel overview;
    private readonly RealtimePageViewModel realtime;
    private readonly TrendPageViewModel trend;
    private readonly AlarmsPageViewModel alarms;
    private readonly HistoryPageViewModel history;
    private readonly SettingsPageViewModel settings;
    private readonly ILogger<PageRefreshCoordinator> logger;
    private readonly DispatcherTimer timer = new()
    {
        Interval = TimeSpan.FromMilliseconds(250)
    };
    private bool refreshing;
    private long lastPoll = -1;
    private int refreshCounter;
    public PageRefreshCoordinator(
        PageContext context,
        StationRuntime runtime,
        MainViewModel shell,
        OverviewPageViewModel overview,
        RealtimePageViewModel realtime,
        TrendPageViewModel trend,
        AlarmsPageViewModel alarms,
        HistoryPageViewModel history,
        SettingsPageViewModel settings,
        ILogger<PageRefreshCoordinator> logger
    )
    {
        this.context = context;
        this.runtime = runtime;
        this.shell = shell;
        this.overview = overview;
        this.realtime = realtime;
        this.trend = trend;
        this.alarms = alarms;
        this.history = history;
        this.settings = settings;
        this.logger = logger;
        context.ConfigurationChanged += ConfigureAsync;
        timer.Tick += OnTick;
    }

    public async Task InitializeAsync()
    {
        await ConfigureAsync();
        await alarms.RefreshAlarmsAsync();
        timer.Start();
    }

    private async Task ConfigureAsync()
    {
        var repository = context.Repository;
        var tags = await repository.TagsAsync();
        List<DeviceRow> devices = [
            new()
            {
                Id = "",
                Name = "全部设备"
            },
            ..await repository.DevicesAsync()
        ];
        context.Communication = await repository.SettingsAsync();
        await settings.ConfigureAsync(tags, devices);
        trend.Configure(tags);
        history.Configure(tags, devices);
        realtime.Configure(tags);
        lastPoll = -1;
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (refreshing)
        {
            return;
        }

        refreshing = true;
        try
        {
            history.AdvanceReplay();
            var snapshot = runtime.Snapshot;
            int interval = context.Communication.PollIntervalMs;
            shell.Refresh(snapshot, interval);
            double age = (DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalMilliseconds;
            bool stale = age > Math.Max(3000, interval * 3);
            bool newPoll = lastPoll != snapshot.PollCount;
            if (newPoll || stale)
            {
                lastPoll = snapshot.PollCount;
                realtime.Refresh(snapshot, stale);
                overview.Refresh(snapshot, stale);
                if (newPoll)
                {
                    trend.Refresh(snapshot);
                }
            }

            if (++refreshCounter % 8 == 0)
            {
                await alarms.RefreshAlarmsAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UI refresh failed");
            context.Status = "刷新失败：" + ex.Message;
        }
        finally
        {
            refreshing = false;
        }
    }

    public void Dispose()
    {
        timer.Stop();
        timer.Tick -= OnTick;
        context.ConfigurationChanged -= ConfigureAsync;
    }
}
