using System.Diagnostics;
using System.IO.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PumpStationMonitor.Core.Alarms;
using PumpStationMonitor.Core.Models;
using PumpStationMonitor.Core.Protocol;
using PumpStationMonitor.Core.Services;
namespace PumpStationMonitor.Data.Services;

public sealed class StationRuntime(StationRepository repository, HistoryWriter writer, ILoggerFactory logs, ILogger<StationRuntime> logger) : IPollingSource, IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IModbusClient? client;
    private List<TagRow> tags = [];
    private List<DeviceRow> devices = [];
    private Dictionary<string, RuleRow> rules = [];
    private AlarmEngine engine = new([]);
    private readonly Queue<AlarmEvent> pendingAlarms = new();
    private CommunicationSettings settings = new();
    private StationSnapshot snapshot = new([], ConnectionState.Disconnected, 0, 0, DateTimeOffset.UtcNow, null);
    public StationSnapshot Snapshot => Volatile.Read(ref snapshot);
    public int IntervalMs => settings.PollIntervalMs;
    public int RetentionDays => settings.RetentionDays;

    public Task ReloadAsync() => ChangeConfigurationAsync(() => Task.CompletedTask);
    public async Task ChangeConfigurationAsync(Func<Task> change)
    {
        await gate.WaitAsync();
        try
        {
            await FlushAlarmsAsync();
            await change();
            await ReloadCoreAsync();
        }
        finally { gate.Release(); }
    }
    private async Task ReloadCoreAsync()
    {
        if (client is not null) await client.DisposeAsync();
        settings = await repository.SettingsAsync();
        tags = (await repository.TagsAsync()).Where(t => t.Enabled).ToList(); devices = await repository.DevicesAsync();
        rules = (await repository.RulesAsync()).Where(r => r.Enabled).ToDictionary(r => r.Id);
        engine = new AlarmEngine(rules.Values.Select(r => new AlarmRule(r.Id, r.TagId, Enum.Parse<AlarmKind>(r.Kind), r.LimitValue, r.Deadband, TimeSpan.FromMilliseconds(r.DelayMs), Enum.Parse<AlarmSeverity>(r.Severity))));
        foreach (var alarm in await repository.AlarmsAsync(activeOnly: true))
            if (rules.ContainsKey(alarm.RuleId)) engine.RestoreActive(alarm.RuleId, Guid.Parse(alarm.Id), alarm.Value, DateTimeOffset.FromUnixTimeMilliseconds(alarm.RaisedAt), alarm.AcknowledgedAt.HasValue);
        var options = new ModbusOptions { Timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs), Retries = settings.Retries };
        client = settings.Mode == "TCP"
            ? new ModbusTcpClient(settings.Host, settings.Port, options, logs.CreateLogger<ModbusTcpClient>())
            : new ModbusRtuClient(new SerialSettings(settings.SerialPort, settings.BaudRate, Enum.Parse<Parity>(settings.Parity), settings.DataBits, Enum.Parse<StopBits>(settings.StopBits)), options, logs.CreateLogger<ModbusRtuClient>());
        client.StateChanged += state => Volatile.Write(ref snapshot, Snapshot with { Connection = state });
        logger.LogInformation("Configuration loaded: {Mode}, {Count} tags, {Interval} ms", settings.Mode, tags.Count, settings.PollIntervalMs);
    }
    public async Task PollAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            await FlushAlarmsAsync();
            var watch = Stopwatch.StartNew(); var values = new List<LiveValue>(); string? error = null;
            var previous = Snapshot.Values.ToDictionary(v => v.Tag.Id);
            bool transportFailed = false;
            foreach (var group in tags.GroupBy(t => (t.DeviceId, t.Function)))
            {
                var now = DateTimeOffset.UtcNow;
                try
                {
                    if (transportFailed) throw new IOException("本轮通信中断，等待下一轮重连");
                    var unit = (byte)devices.Single(d => d.Id == group.Key.DeviceId).UnitId;
                    // 同设备、同功能码的相邻地址合并；位点共用同一个状态寄存器。
                    foreach (var block in ReadBlocks(group))
                    {
                        int address=block.Min(t=>t.Address),end=block.Max(t=>t.Address+(t.DataType=="Float32"?2:1));
                        bool[]? bits=null;ushort[]? words=null;
                        if(group.Key.Function is 1 or 2) bits=await client!.ReadBitsAsync(unit,(byte)group.Key.Function,(ushort)address,(ushort)(end-address),token);
                        else words=await client!.ReadRegistersAsync(unit,(byte)group.Key.Function,(ushort)address,(ushort)(end-address),token);
                        foreach(var tag in block)
                        {
                            int index=tag.Address-address;
                            double raw=bits is not null?(bits[index]?1:0):tag.Bit.HasValue?(RegisterConverter.Bit(words![index],tag.Bit.Value)?1:0):tag.DataType switch
                            {
                                "Float32"=>RegisterConverter.Float32(words![index],words[index+1],Enum.Parse<RegisterOrder>(tag.ByteOrder)),
                                "Int16"=>unchecked((short)words![index]),_=>words![index]
                            };
                            double value=RegisterConverter.Scale(raw,tag.Scale,tag.Offset);
                            var quality=double.IsFinite(value)?DataQuality.Good:DataQuality.Bad;
                            if(!double.IsFinite(value))value=previous.GetValueOrDefault(tag.Id)?.Value??0;
                            values.Add(new(tag,value,quality,DateTimeOffset.UtcNow));
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or System.Net.Sockets.SocketException or ModbusException or UnauthorizedAccessException)
                {
                    logger.LogError(ex, "Polling device {Device} failed", group.Key.DeviceId); error = ex.Message;
                    transportFailed = ex is not ModbusException;
                    foreach (var tag in group.Where(t => values.All(v => v.Tag.Id != t.Id))) values.Add(new(tag, previous.GetValueOrDefault(tag.Id)?.Value ?? 0, DataQuality.Bad, now));
                }
            }
            foreach (var value in values)
            {
                await writer.EnqueueAsync(new() { TagId = value.Tag.Id, DeviceId = value.Tag.DeviceId, Timestamp = value.Timestamp.ToUnixTimeMilliseconds(), Value = value.Value, Quality = value.Quality.ToString() }, token);
                foreach (var alarm in engine.Evaluate(new(value.Tag.Id, value.Value, value.Quality, value.Timestamp))) pendingAlarms.Enqueue(alarm);
            }
            await FlushAlarmsAsync();
            Volatile.Write(ref snapshot, new(values, client!.State, watch.Elapsed.TotalMilliseconds, Snapshot.PollCount + 1, DateTimeOffset.UtcNow, error));
            logger.LogInformation("Poll {Cycle}: {Count} tags, {Elapsed:F2} ms, quality {Quality}", Snapshot.PollCount, values.Count, watch.Elapsed.TotalMilliseconds, error is null ? "Good" : "Bad");
        }
        finally { gate.Release(); }
    }
    private static IEnumerable<List<TagRow>> ReadBlocks(IEnumerable<TagRow> source)
    {
        var block=new List<TagRow>();int start=0,end=0;
        foreach(var tag in source.OrderBy(t=>t.Address))
        {
            int nextEnd=tag.Address+(tag.DataType=="Float32"?2:1),maximum=tag.Function is 1 or 2?2000:125;
            if(block.Count>0&&(nextEnd-start>maximum||tag.Address>end))
            {
                yield return block;block=[];
            }
            if(block.Count==0)start=tag.Address;
            block.Add(tag);end=Math.Max(end,nextEnd);
        }
        if(block.Count>0)yield return block;
    }
    private async Task FlushAlarmsAsync()
    {
        while (pendingAlarms.TryPeek(out var alarm))
        {
            var rule = rules[alarm.RuleId];
            if (alarm.Transition == AlarmTransition.Raised)
                await repository.RaiseAlarmAsync(new() { Id = alarm.OccurrenceId.ToString(), RuleId = alarm.RuleId, TagId = rule.TagId, Name = tags.FirstOrDefault(t => t.Id == rule.TagId)?.Name ?? rule.TagId, Severity = alarm.Severity.ToString(), RaisedAt = alarm.Timestamp.ToUnixTimeMilliseconds(), Value = alarm.Value });
            else if (alarm.Transition == AlarmTransition.Recovered) await repository.RecoverAlarmAsync(alarm.OccurrenceId.ToString(), alarm.Timestamp.ToUnixTimeMilliseconds());
            logger.LogWarning("Alarm {Rule} {Transition} occurrence {Id}", alarm.RuleId, alarm.Transition, alarm.OccurrenceId);
            pendingAlarms.Dequeue();
        }
    }
    public async Task ControlAsync(int pump, bool run, string mode, CancellationToken token = default)
    {
        if (pump is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(pump));
        await gate.WaitAsync(token);
        try
        {
            if (client is null) throw new InvalidOperationException("采集服务尚未就绪，请稍后重试");
            byte unit = (byte)devices.Single(d => d.Id == $"pump{pump}").UnitId;
            if (mode == "Register") await client!.WriteRegisterAsync(unit, (ushort)(39 + pump), run ? (ushort)1 : (ushort)0, token);
            else await client!.WriteCoilAsync(unit, (ushort)(pump - 1), run, token);
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync() { await gate.WaitAsync(); try { if (client is not null) await client.DisposeAsync(); } finally { gate.Release(); } }
}
