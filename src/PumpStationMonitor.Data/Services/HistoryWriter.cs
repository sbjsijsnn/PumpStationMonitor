using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PumpStationMonitor.Core.Services;
namespace PumpStationMonitor.Data.Services;

public sealed class HistoryWriter(StationRepository repository, ILogger<HistoryWriter> logger) : BackgroundService
{
    private readonly Channel<HistoryRow> queue = Channel.CreateBounded<HistoryRow>(new BoundedChannelOptions(20000) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    public long Written => Interlocked.Read(ref written);
    private long written;
    public int Pending => queue.Reader.Count;
    public ValueTask EnqueueAsync(HistoryRow row, CancellationToken token) => queue.Writer.WriteAsync(row, token);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<HistoryRow>(200);
        Task tick = Task.Delay(1000, stoppingToken);
        Task<bool> readable = queue.Reader.WaitToReadAsync(stoppingToken).AsTask();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.WhenAny(tick, readable);
                while (batch.Count < 200 && queue.Reader.TryRead(out var row)) batch.Add(row);
                if (batch.Count == 200 || tick.IsCompleted)
                {
                    await FlushAsync(batch, stoppingToken);
                    tick = Task.Delay(1000, stoppingToken);
                }
                if (readable.IsCompleted)
                {
                    if (!await readable) break;
                    readable = queue.Reader.WaitToReadAsync(stoppingToken).AsTask();
                }
            }
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested) { logger.LogDebug(ex, "History writer stopping"); }
        finally
        {
            // 停止顺序先取消采集，再关闭生产端；最后批次必须落库。
            queue.Writer.TryComplete();
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            do
            {
                while (batch.Count < 200 && queue.Reader.TryRead(out var row)) batch.Add(row);
                await FlushAsync(batch, drain.Token);
            } while (queue.Reader.Count > 0);
        }
    }
    private async Task FlushAsync(List<HistoryRow> batch, CancellationToken token)
    {
        if (batch.Count == 0) return;
        for (int attempt = 0; ; attempt++)
        {
            try { await repository.InsertHistoryAsync(batch, token); Interlocked.Add(ref written, batch.Count); batch.Clear(); return; }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                logger.LogError(ex, "History persistence failed; retaining {Count} samples", batch.Count);
                await Task.Delay(Math.Min(3000, 200 * (attempt + 1)), token);
            }
        }
    }
}
