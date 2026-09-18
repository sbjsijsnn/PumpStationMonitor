using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using PumpStationMonitor.App.ViewModels;
using PumpStationMonitor.App.Views;
using PumpStationMonitor.Data.Security;

namespace PumpStationMonitor.App;

// 自动切换既有页面并保存界面冒烟检查证据。
// 复用正常登录与页面命令，验证窗口装配后的完整交互路径。
// 使用调用端指定的隔离目录，截图与数据不会混入日常运行。
internal static class UiSmoke
{
    public static async Task RunAsync(IServiceProvider services, string folder)
    {
        Directory.CreateDirectory(folder);
        var window = services.GetRequiredService<MainWindow>();
        var model = services.GetRequiredService<MainViewModel>();
        var overview = services.GetRequiredService<OverviewPageViewModel>();
        var realtime = services.GetRequiredService<RealtimePageViewModel>();
        var trend = services.GetRequiredService<TrendPageViewModel>();
        var alarms = services.GetRequiredService<AlarmsPageViewModel>();
        var history = services.GetRequiredService<HistoryPageViewModel>();
        var settings = services.GetRequiredService<SettingsPageViewModel>();
        var context = services.GetRequiredService<PageContext>();
        if (!window.IsVisible)
        {
            throw new InvalidOperationException("Main window did not open after login");
        }

        await overview.ControlCommand.ExecuteAsync("1:start");
        await Task.Delay(1500);
        for (int page = 0; page < 6; page++)
        {
            model.Page = page;
            if (page == 4)
            {
                context.TimeRange.ToText = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-dd HH:mm:ss");
                await history.QueryHistoryCommand.ExecuteAsync(null);
            }

            await Task.Delay(500);
            window.UpdateLayout();
            await CaptureAsync(window, Path.Combine(folder, $"page-{page}.png"));
            if (page == 3)
            {
                if (alarms.ActiveAlarms.Count > 0)
                {
                    alarms.SelectedAlarm = alarms.ActiveAlarms[0];
                    await alarms.AcknowledgeCommand.ExecuteAsync(null);
                }

                await alarms.QueryAlarmsCommand.ExecuteAsync(null);
                window.AlarmsView.AlarmTabs.SelectedIndex = 1;
                await Task.Delay(250);
                await CaptureAsync(window, Path.Combine(folder, "alarm-history.png"));
            }

            if (page == 5)
            {
                for (int tab = 1; tab < 4; tab++)
                {
                    window.SettingsView.SettingsTabs.SelectedIndex = tab;
                    await Task.Delay(250);
                    await CaptureAsync(window, Path.Combine(folder, $"settings-{tab}.png"));
                }
            }
        }

        await overview.ControlCommand.ExecuteAsync("1:stop");
        var session = services.GetRequiredService<SessionService>();
        session.Logout();
        await session.LoginAsync("operator", "Operator123!");
        await context.RefreshSessionAsync();
        model.Page = 0;
        await Task.Delay(250);
        await CaptureAsync(window, Path.Combine(folder, "operator.png"));
        await File.WriteAllTextAsync(
            Path.Combine(folder, "ui-smoke.txt"),
            $"pages=6; liveRows={realtime.LiveRows.Count}; " +
            $"historyRows={history.HistoryRows.Count}; status={context.Status}"
        );
    }

    public static async Task CaptureAsync(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap(
            (int)content.ActualWidth,
            (int)content.ActualHeight,
            96,
            96,
            PixelFormats.Pbgra32
        );
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var file = File.Create(path);
        encoder.Save(file);
    }
}
