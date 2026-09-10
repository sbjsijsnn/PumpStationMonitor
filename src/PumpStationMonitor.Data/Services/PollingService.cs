using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace PumpStationMonitor.Data.Services;

public sealed class PollingService(StationRuntime runtime, StationRepository repository, ILogger<PollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset cleanup = DateTimeOffset.MinValue;
        try
        {
            await runtime.ReloadAsync();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(runtime.IntervalMs));
            do
            {
                try
                {
                    await runtime.PollAsync(stoppingToken);
                    if (DateTimeOffset.UtcNow - cleanup > TimeSpan.FromHours(1)) { await repository.CleanupAsync(runtime.RetentionDays, stoppingToken); cleanup = DateTimeOffset.UtcNow; }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { logger.LogError(ex, "Polling cycle failed"); }
                var desired=TimeSpan.FromMilliseconds(runtime.IntervalMs);
                if(timer.Period!=desired)timer.Period=desired;
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested) { logger.LogInformation(ex, "Polling stopped"); }
    }
}
