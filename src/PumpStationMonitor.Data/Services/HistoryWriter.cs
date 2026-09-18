using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PumpStationMonitor.Core.Services;

namespace PumpStationMonitor.Data.Services;

// 消费采集产生的历史记录并批量写入数据库。
// 有界队列提供背压，数据库短暂故障时保留尚未写入的批次。
// 停止阶段单独排空队列，避免正常退出丢失最后一批数据。
public sealed class HistoryWriter(StationRepository repository, ILogger<HistoryWriter> logger) : BackgroundService
{
    private readonly Channel<HistoryRow> queue = Channel.CreateBounded<HistoryRow>(
        new BoundedChannelOptions(20000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        }
    );
    public long Written => Interlocked.Read(ref written);

    private long written;
    public int Pending => queue.Reader.Count;

    public ValueTask EnqueueAsync(HistoryRow row, CancellationToken token) => queue.Writer.WriteAsync(row, token);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 把队列数据汇成最多两百条的批次，降低每条记录单独提交的成本。
        var batch = new List<HistoryRow>(200);
        Task tick = Task.Delay(1000, stoppingToken);
        Task<bool> readable = queue.Reader.WaitToReadAsync(stoppingToken).AsTask();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // 等待数据或一秒期限；复用未结束的等待，避免累积后台等待任务。
                await Task.WhenAny(tick, readable);
                while (batch.Count < 200 && queue.Reader.TryRead(out var row))
                {
                    batch.Add(row);
                }

                // 达到数量或时间边界即落库，写入成功才由 FlushAsync 清空批次。
                if (batch.Count == 200 || tick.IsCompleted)
                {
                    await FlushAsync(batch, stoppingToken);
                    tick = Task.Delay(1000, stoppingToken);
                }

                // 只替换已结束的可读等待；生产端关闭时进入最后的排空流程。
                if (readable.IsCompleted)
                {
                    if (!await readable)
                    {
                        break;
                    }

                    readable = queue.Reader.WaitToReadAsync(stoppingToken).AsTask();
                }
            }
        }
        // 正常停止只结束主循环，尚未提交的数据仍由 finally 保存。
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogDebug(ex, "History writer stopping");
        }
        finally
        {
            // 停止顺序先取消采集，再关闭生产端；最后批次必须落库。
            queue.Writer.TryComplete();
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            do
            {
                while (batch.Count < 200 && queue.Reader.TryRead(out var row))
                {
                    batch.Add(row);
                }

                await FlushAsync(batch, drain.Token);
            }
            while (queue.Reader.Count > 0);
        }
    }

    private async Task FlushAsync(List<HistoryRow> batch, CancellationToken token)
    {
        if (batch.Count == 0)
        {
            return;
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await repository.InsertHistoryAsync(batch, token);
                Interlocked.Add(ref written, batch.Count);
                batch.Clear();
                return;
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
            {
                logger.LogError(ex, "History persistence failed; retaining {Count} samples", batch.Count);
                await Task.Delay(Math.Min(3000, 200 * (attempt + 1)), token);
            }
        }
    }
}
