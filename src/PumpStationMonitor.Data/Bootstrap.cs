using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PumpStationMonitor.Core.Services;
using PumpStationMonitor.Data.Security;
using PumpStationMonitor.Data.Services;
using Serilog;

namespace PumpStationMonitor.Data;

// 集中配置宿主、日志、数据库和后台服务。
// 应用与控制台宿主复用同一装配流程，降低运行环境差异。
// 首次初始化数据库再启动服务，保证采集时配置已经可用。
public static class Bootstrap
{
    public static HostApplicationBuilder Create(string[] args, string? databasePath = null)
    {
        var builder = Host.CreateApplicationBuilder(
            new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory }
        );
        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        string folder = Environment.GetEnvironmentVariable("PUMP_DATA_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PumpStationMonitor"
        );
        Directory.CreateDirectory(folder);
        builder.Services.AddSingleton(
            new StationRepository(databasePath ?? Path.Combine(folder, "station.db"))
        );
        builder.Services.AddSingleton<SessionService>();
        builder.Services.AddSingleton<StationFacade>();
        builder.Services.AddSingleton<HistoryWriter>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HistoryWriter>());
        builder.Services.AddSingleton<StationRuntime>();
        builder.Services.AddSingleton<IPollingSource>(sp => sp.GetRequiredService<StationRuntime>());
        builder.Services.AddHostedService<PollingService>();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        builder.Services.AddSerilog(
            (services, configuration) => configuration.MinimumLevel.Debug().MinimumLevel.Override("Microsoft", Serilog
                .Events.LogEventLevel.Warning).Enrich.FromLogContext().WriteTo.File(
                Path.Combine(folder, "logs", "station-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true
            )
        );
        return builder;
    }

    public static async Task InitializeAsync(IHost host, IConfiguration configuration)
    {
        var defaults = configuration.GetSection("Communication").Get<CommunicationSettings>() ?? new();
        await host.Services.GetRequiredService<StationRepository>().InitializeAsync(defaults);
    }
}
