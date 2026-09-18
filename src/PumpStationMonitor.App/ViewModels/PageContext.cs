using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using PumpStationMonitor.Core.Services;
using PumpStationMonitor.Data;
using PumpStationMonitor.Data.Security;

namespace PumpStationMonitor.App.ViewModels;

// 共享当前登录身份、操作提示、通信配置和查询时间范围。
// 让页面协作依赖明确的小型上下文，避免通过主窗口交换业务状态。
// 配置刷新集中通知协调器，保存后所有页面使用同一份最新点表。
public partial class PageContext(
    StationRepository repository,
    StationFacade facade,
    SessionService session,
    ILogger<PageContext> logger
) : ObservableObject
{
    public StationRepository Repository { get; } = repository;
    public StationFacade Facade { get; } = facade;
    public CommunicationSettings Communication
    {
        get;
        set;
    } = new();
    public QueryTimeRange TimeRange { get; } = new();
    public bool IsAdmin => session.IsAdmin;
    public string UserLabel => $"{session.Username} · {(IsAdmin ? "管理员" : "操作员")}";

    [ObservableProperty]
    private string status = "正在读取泵站数据…";
    public event Func<Task>? ConfigurationChanged;
    public async Task RefreshConfigurationAsync()
    {
        if (ConfigurationChanged is { } refresh)
        {
            await refresh();
        }
    }

    public async Task RefreshSessionAsync()
    {
        OnPropertyChanged(nameof(IsAdmin));
        OnPropertyChanged(nameof(UserLabel));
        await RefreshConfigurationAsync();
    }

    public async Task RunAsync(Func<Task> action, string success)
    {
        try
        {
            await action();
            Status = success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "User action failed");
            Status = ex.Message;
        }
    }
}
