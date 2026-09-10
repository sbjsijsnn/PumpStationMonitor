using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using PumpStationMonitor.App.ViewModels;
using PumpStationMonitor.App.Views;
using PumpStationMonitor.Data.Security;
namespace PumpStationMonitor.App;

internal static class UiSmoke
{
    public static async Task RunAsync(IServiceProvider services, string folder)
    {
        Directory.CreateDirectory(folder); var window = services.GetRequiredService<MainWindow>(); var model = services.GetRequiredService<MainViewModel>();
        if (!window.IsVisible) throw new InvalidOperationException("Main window did not open after login");
        await model.ControlCommand.ExecuteAsync("1:start"); await Task.Delay(1500);
        for (int page = 0; page < 6; page++)
        {
            model.Page = page;
            if (page == 4) { model.ToText = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-dd HH:mm:ss"); await model.QueryHistoryCommand.ExecuteAsync(null); }
            await Task.Delay(500); window.UpdateLayout();
            await CaptureAsync(window, Path.Combine(folder, $"page-{page}.png"));
            if (page == 3)
            {
                if (model.ActiveAlarms.Count > 0) { model.SelectedAlarm = model.ActiveAlarms[0]; await model.AcknowledgeCommand.ExecuteAsync(null); }
                await model.QueryAlarmsCommand.ExecuteAsync(null); window.AlarmTabs.SelectedIndex = 1; await Task.Delay(250); await CaptureAsync(window, Path.Combine(folder, "alarm-history.png"));
            }
            if (page == 5)
            {
                for (int tab = 1; tab < 4; tab++) { window.SettingsTabs.SelectedIndex = tab; await Task.Delay(250); await CaptureAsync(window, Path.Combine(folder, $"settings-{tab}.png")); }
            }
        }
        await model.ControlCommand.ExecuteAsync("1:stop");
        var session = services.GetRequiredService<SessionService>(); session.Logout(); await session.LoginAsync("operator", "Operator123!"); await model.RefreshSessionAsync(); model.Page = 0; await Task.Delay(250); await CaptureAsync(window, Path.Combine(folder, "operator.png"));
        await File.WriteAllTextAsync(Path.Combine(folder, "ui-smoke.txt"), $"pages=6; liveRows={model.LiveRows.Count}; historyRows={model.HistoryRows.Count}; status={model.Status}");
    }
    public static async Task CaptureAsync(Window window, string path)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); await using var file = File.Create(path); encoder.Save(file);
    }
}
