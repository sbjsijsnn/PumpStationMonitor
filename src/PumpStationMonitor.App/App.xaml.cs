using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PumpStationMonitor.App.ViewModels;
using PumpStationMonitor.App.Views;
using PumpStationMonitor.Data;
using PumpStationMonitor.Data.Security;

namespace PumpStationMonitor.App;

// 建立应用宿主并衔接登录、主窗口和后台服务生命周期。
// 依赖集中注册，页面和运行服务共享受管理的实例。
// 关闭时先停止刷新和宿主，再退出界面以保存最后的历史数据。
public partial class App : Application
{
    private IHost? host;
    private bool closing;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var builder = Bootstrap.Create([]);
            builder.Services.AddSingleton<LoginViewModel>();
            builder.Services.AddSingleton<LoginWindow>();
            builder.Services.AddSingleton<MainViewModel>();
            builder.Services.AddSingleton<MainWindow>();
            builder.Services.AddSingleton<PageContext>();
            builder.Services.AddSingleton<PageRefreshCoordinator>();
            builder.Services.AddSingleton<OverviewPageViewModel>();
            builder.Services.AddSingleton<RealtimePageViewModel>();
            builder.Services.AddSingleton<TrendPageViewModel>();
            builder.Services.AddSingleton<AlarmsPageViewModel>();
            builder.Services.AddSingleton<HistoryPageViewModel>();
            builder.Services.AddSingleton<SettingsPageViewModel>();
            host = builder.Build();
            await Bootstrap.InitializeAsync(host, builder.Configuration);
            await host.StartAsync();
            DispatcherUnhandledException += (_, eventArgs) =>
            {
                host.Services.GetRequiredService<ILogger<App>>().LogCritical(eventArgs.Exception,
                    "Unhandled UI exception");
            };
            var login = host.Services.GetRequiredService<LoginWindow>();
            host.Services.GetRequiredService<LoginViewModel>().SignedIn += async () =>
            {
                try
                {
                    var window = host.Services.GetRequiredService<MainWindow>();
                    MainWindow = window;
                    await host.Services.GetRequiredService<PageRefreshCoordinator>().InitializeAsync();
                    window.Closing += OnClosing;
                    window.Show();
                    login.Close();
                }
                catch (Exception ex)
                {
                    host.Services.GetRequiredService<ILogger<App>>().LogError(ex, "Opening main window failed");
                    MessageBox.Show(ex.Message, "打开工作站失败");
                }
            };
            login.Closed += async (_, _) =>
            {
                if (MainWindow == login)
                {
                    await StopAsync();
                }
            };
            MainWindow = login;
            login.Show();
            if (e.Args.Contains("--ui-smoke") || e.Args.Contains("--ui-endurance"))
            {
                // 冒烟模式仍走正常密码验证，并由调用端指定隔离数据库目录。
                if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PUMP_DATA_DIR")))
                {
                    throw new InvalidOperationException("UI smoke requires isolated PUMP_DATA_DIR");
                }

                await Task.Delay(300);
                await UiSmoke.CaptureAsync(
                    login,
                    Path.Combine(Environment.GetEnvironmentVariable("PUMP_DATA_DIR")!, "login.png")
                );
                await host.Services.GetRequiredService<LoginViewModel>().LoginCommand.ExecuteAsync("Admin123!");
                await Task.Delay(2000);
                if (e.Args.Contains("--ui-endurance"))
                {
                    await UiEndurance.RunAsync(host.Services, Environment.GetEnvironmentVariable("PUMP_DATA_DIR")!);
                }
                else
                {
                    await UiSmoke.RunAsync(host.Services, Environment.GetEnvironmentVariable("PUMP_DATA_DIR")!);
                }

                await StopAsync();
            }
        }
        catch (Exception ex)
        {
            string directory = Environment.GetEnvironmentVariable("PUMP_DATA_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PumpStationMonitor"
            );
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(Path.Combine(directory, "startup-error.log"), ex + Environment.NewLine);
            if (!e.Args.Contains("--ui-smoke") && !e.Args.Contains("--ui-endurance"))
            {
                MessageBox.Show("启动失败：" + ex.Message, "泵站设备监控");
            }

            await StopAsync(1);
        }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (closing)
        {
            return;
        }

        e.Cancel = true;
        await StopAsync();
    }

    private async Task StopAsync(int code = 0)
    {
        if (closing)
        {
            return;
        }

        closing = true;
        try
        {
            if (host is not null)
            {
                host.Services.GetService<PageRefreshCoordinator>()?.Dispose();
                await host.StopAsync();
                if (host is IAsyncDisposable asyncHost)
                {
                    await asyncHost.DisposeAsync();
                }
                else
                {
                    host.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            await File.AppendAllTextAsync(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PumpStationMonitor",
                    "shutdown-error.log"
                ),
                ex + Environment.NewLine
            );
            code = 1;
        }

        Shutdown(code);
    }
}
