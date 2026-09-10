using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PumpStationMonitor.App.ViewModels;
using PumpStationMonitor.App.Views;
using PumpStationMonitor.Data;
using PumpStationMonitor.Data.Services;
namespace PumpStationMonitor.App;
internal static class UiEndurance
{
    public static async Task RunAsync(IServiceProvider services,string folder)
    {
        int seconds=int.TryParse(Environment.GetEnvironmentVariable("PUMP_ENDURANCE_SECONDS"),out int value)?value:7200;
        var model=services.GetRequiredService<MainViewModel>();var window=services.GetRequiredService<MainWindow>();
        var runtime=services.GetRequiredService<StationRuntime>();var writer=services.GetRequiredService<HistoryWriter>();
        model.Page=2;model.SpanMinutes=60;
        var clock=Stopwatch.StartNew();using var process=Process.GetCurrentProcess();var previousCpu=process.TotalProcessorTime;double previousTime=0;long peak=0;int errors=0;
        await using(var csv=new StreamWriter(Path.Combine(folder,"ui-runtime.csv"),false,System.Text.Encoding.UTF8))
        {
            await csv.WriteLineAsync("elapsed_seconds,working_set_mb,private_mb,cpu_percent,poll_count,poll_ms,history_written,queue_pending,quality");
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(1));
            while(clock.Elapsed.TotalSeconds<seconds && await timer.WaitForNextTickAsync())
            {
                process.Refresh();double now=clock.Elapsed.TotalSeconds;var cpu=process.TotalProcessorTime;double percent=(cpu-previousCpu).TotalSeconds/(now-previousTime)/Environment.ProcessorCount*100;previousCpu=cpu;previousTime=now;
                var snapshot=runtime.Snapshot;peak=Math.Max(peak,process.WorkingSet64);if(snapshot.Error is not null)errors++;
                await csv.WriteLineAsync(FormattableString.Invariant($"{now:F3},{process.WorkingSet64/1048576.0:F2},{process.PrivateMemorySize64/1048576.0:F2},{percent:F3},{snapshot.PollCount},{snapshot.PollMilliseconds:F3},{writer.Written},{writer.Pending},{(snapshot.Error is null?"Good":"Bad")}"));
                if((int)now%10==0)await csv.FlushAsync();
            }
        }
        await UiSmoke.CaptureAsync(window,Path.Combine(folder,"endurance-trend.png"));
        await File.WriteAllTextAsync(Path.Combine(folder,"ui-summary.json"),JsonSerializer.Serialize(new{completed=true,requestedSeconds=seconds,elapsedSeconds=clock.Elapsed.TotalSeconds,polls=runtime.Snapshot.PollCount,historyRows=await services.GetRequiredService<StationRepository>().HistoryCountAsync(),peakWorkingSetMb=peak/1048576.0,badSampleSeconds=errors,processors=Environment.ProcessorCount},new JsonSerializerOptions{WriteIndented=true}));
    }
}
