using System.Windows;
using PumpStationMonitor.App.ViewModels;

namespace PumpStationMonitor.App.Views;

// 组织侧边导航、公共状态栏和六个独立页面。
// 在组合位置为页面接入各自的视图模型，页面间不通过窗口处理业务。
// 窗口本身只负责控件装配，生命周期由应用入口管理。
public partial class MainWindow : Window
{
    public PageContext Workspace
    {
        get;
    }

    public MainWindow(
        MainViewModel model,
        PageContext workspace,
        OverviewPageViewModel overview,
        RealtimePageViewModel realtime,
        TrendPageViewModel trend,
        AlarmsPageViewModel alarms,
        HistoryPageViewModel history,
        SettingsPageViewModel settings
    )
    {
        Workspace = workspace;
        InitializeComponent();
        DataContext = model;
        OverviewView.DataContext = overview;
        RealtimeView.DataContext = realtime;
        TrendView.DataContext = trend;
        AlarmsView.DataContext = alarms;
        HistoryView.DataContext = history;
        SettingsView.DataContext = settings;
    }
}
