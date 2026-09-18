using System.Diagnostics;
using System.IO.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PumpStationMonitor.Core.Alarms;
using PumpStationMonitor.Core.Models;
using PumpStationMonitor.Core.Protocol;
using PumpStationMonitor.Core.Services;

namespace PumpStationMonitor.Data.Services;

// 协调点表读取、历史入队、报警评估和控制请求。
// 以同一把异步锁隔离配置变化与设备访问，保证一轮采集的一致性。
// 通过不可变快照向界面交付结果，让展示线程无需接触通信连接。
public sealed class StationRuntime(
    StationRepository repository,
    HistoryWriter writer,
    ILoggerFactory logs,
    ILogger<StationRuntime> logger
) : IPollingSource, IAsyncDisposable
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
        finally
        {
            gate.Release();
        }
    }

    private async Task ReloadCoreAsync()
    {
        if (client is not null)
        {
            await client.DisposeAsync();
        }

        settings = await repository.SettingsAsync();
        tags = (await repository.TagsAsync()).Where(t => t.Enabled).ToList();
        devices = await repository.DevicesAsync();
        rules = (await repository.RulesAsync()).Where(r => r.Enabled).ToDictionary(r => r.Id);
        var alarmRules = rules.Values.Select(CreateAlarmRule);
        engine = new AlarmEngine(alarmRules);
        foreach (var alarm in await repository.AlarmsAsync(activeOnly: true))
        {
            if (rules.ContainsKey(alarm.RuleId))
            {
                engine.RestoreActive(
                    alarm.RuleId,
                    Guid.Parse(alarm.Id),
                    alarm.Value,
                    DateTimeOffset.FromUnixTimeMilliseconds(alarm.RaisedAt),
                    alarm.AcknowledgedAt.HasValue
                );
            }
        }

        var options = new ModbusOptions
        {
            Timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs),
            Retries = settings.Retries
        };
        client = CreateClient(options);
        client.StateChanged += state => Volatile.Write(ref snapshot, Snapshot with { Connection = state });
        logger.LogInformation(
            "Configuration loaded: {Mode}, {Count} tags, {Interval} ms",
            settings.Mode,
            tags.Count,
            settings.PollIntervalMs
        );
    }

    private static AlarmRule CreateAlarmRule(RuleRow rule)
    {
        var kind = Enum.Parse<AlarmKind>(rule.Kind);
        var severity = Enum.Parse<AlarmSeverity>(rule.Severity);
        var delay = TimeSpan.FromMilliseconds(rule.DelayMs);
        return new AlarmRule(rule.Id, rule.TagId, kind, rule.LimitValue, rule.Deadband, delay, severity);
    }

    private IModbusClient CreateClient(ModbusOptions options)
    {
        if (settings.Mode == "TCP")
        {
            var tcpLogger = logs.CreateLogger<ModbusTcpClient>();
            return new ModbusTcpClient(settings.Host, settings.Port, options, tcpLogger);
        }
        var parity = Enum.Parse<Parity>(settings.Parity);
        var stopBits = Enum.Parse<StopBits>(settings.StopBits);
        var serial = new SerialSettings(
            settings.SerialPort, settings.BaudRate, parity, settings.DataBits, stopBits);
        var rtuLogger = logs.CreateLogger<ModbusRtuClient>();
        return new ModbusRtuClient(serial, options, rtuLogger);
    }

    public async Task PollAsync(CancellationToken token)
    {
        // 串行化采集与配置、控制，避免一轮读取混用两份点表。
        await gate.WaitAsync(token);
        try
        {
            // 先持久化上轮尚未落库的报警，成功后再开始本轮采样。
            await FlushAlarmsAsync();
            var watch = Stopwatch.StartNew();
            var values = new List<LiveValue>();
            string? error = null;
            // 保留上一轮值只用于坏质量占位，不能把旧值当成新的正常采样。
            var previous = Snapshot.Values.ToDictionary(v => v.Tag.Id);
            bool transportFailed = false;
            foreach (var group in tags.GroupBy(t => (t.DeviceId, t.Function)))
            {
                var now = DateTimeOffset.UtcNow;
                try
                {
                    if (transportFailed)
                    {
                        throw new IOException("本轮通信中断，等待下一轮重连");
                    }

                    var device = devices.Single(d => d.Id == group.Key.DeviceId);
                    byte unit = (byte)device.UnitId;
                    // 同设备、同功能码的相邻地址合并；位点共用同一个状态寄存器。
                    foreach (var block in ReadBlocks(group))
                    {
                        int address = block.Min(t => t.Address);
                        int end = block.Max(t => t.Address + RegisterWidth(t));
                        byte function = (byte)group.Key.Function;
                        ushort startAddress = (ushort)address;
                        ushort count = (ushort)(end - address);
                        bool[]? bits = null;
                        ushort[]? words = null;
                        if (group.Key.Function is 1 or 2)
                        {
                            bits = await client!.ReadBitsAsync(unit, function, startAddress, count, token);
                        }
                        else
                        {
                            words = await client!.ReadRegistersAsync(unit, function, startAddress, count, token);
                        }

                        // 按点位类型解码并缩放；非有限数保留旧值，同时明确标记坏质量。
                        foreach (var tag in block)
                        {
                            int index = tag.Address - address;
                            double raw = DecodeRawValue(tag, index, bits, words);
                            double value = RegisterConverter.Scale(raw, tag.Scale, tag.Offset);
                            bool finite = double.IsFinite(value);
                            var quality = finite ? DataQuality.Good : DataQuality.Bad;
                            if (!finite)
                            {
                                value = previous.GetValueOrDefault(tag.Id)?.Value ?? 0;
                            }

                            values.Add(new(tag, value, quality, DateTimeOffset.UtcNow));
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException
                    or System.Net.Sockets.SocketException or ModbusException or UnauthorizedAccessException)
                {
                    // 运输层故障后跳过本轮剩余请求；设备异常应答只影响当前设备组。
                    logger.LogError(ex, "Polling device {Device} failed", group.Key.DeviceId);
                    error = ex.Message;
                    transportFailed = ex is not ModbusException;
                    foreach (var tag in group.Where(t => values.All(v => v.Tag.Id != t.Id)))
                    {
                        values.Add(
                            new(
                                tag,
                                previous.GetValueOrDefault(tag.Id)?.Value ?? 0,
                                DataQuality.Bad,
                                now
                            )
                        );
                    }
                }
            }

            // 同一份采样分别交给历史队列和报警状态机，保持时间与质量一致。
            foreach (var value in values)
            {
                var history = new HistoryRow
                {
                    TagId = value.Tag.Id,
                    DeviceId = value.Tag.DeviceId,
                    Timestamp = value.Timestamp.ToUnixTimeMilliseconds(),
                    Value = value.Value,
                    Quality = value.Quality.ToString()
                };
                await writer.EnqueueAsync(history, token);
                var sample = new TagSample(value.Tag.Id, value.Value, value.Quality, value.Timestamp);
                var transitions = engine.Evaluate(sample);
                foreach (var alarm in transitions)
                {
                    pendingAlarms.Enqueue(alarm);
                }
            }

            await FlushAlarmsAsync();
            // 报警落库后一次性发布完整快照，界面不会读到半轮数据。
            var completed = new StationSnapshot(
                values,
                client!.State,
                watch.Elapsed.TotalMilliseconds,
                Snapshot.PollCount + 1,
                DateTimeOffset.UtcNow,
                error
            );
            Volatile.Write(ref snapshot, completed);
            logger.LogInformation(
                "Poll {Cycle}: {Count} tags, {Elapsed:F2} ms, quality {Quality}",
                Snapshot.PollCount,
                values.Count,
                watch.Elapsed.TotalMilliseconds,
                error is null ? "Good" : "Bad"
            );
        }
        finally
        {
            // 无论取消还是异常，都必须释放锁，让后续控制或关闭可以继续。
            gate.Release();
        }
    }

    private static int RegisterWidth(TagRow tag) => tag.DataType == "Float32" ? 2 : 1;
    private static double DecodeRawValue(TagRow tag, int index, bool[]? bits, ushort[]? words)
    {
        if (bits is not null)
        {
            return bits[index] ? 1 : 0;
        }

        ushort word = words![index];
        if (tag.Bit.HasValue)
        {
            bool enabled = RegisterConverter.Bit(word, tag.Bit.Value);
            return enabled ? 1 : 0;
        }

        if (tag.DataType == "Float32")
        {
            var order = Enum.Parse<RegisterOrder>(tag.ByteOrder);
            return RegisterConverter.Float32(word, words[index + 1], order);
        }

        return tag.DataType == "Int16" ? unchecked((short)word) : word;
    }

    private static IEnumerable<List<TagRow>> ReadBlocks(IEnumerable<TagRow> source)
    {
        var block = new List<TagRow>();
        int start = 0;
        int end = 0;
        foreach (var tag in source.OrderBy(t => t.Address))
        {
            int nextEnd = tag.Address + RegisterWidth(tag);
            int maximum = tag.Function is 1 or 2 ? 2000 : 125;
            if (block.Count > 0 && (nextEnd - start > maximum || tag.Address > end))
            {
                yield return block;
                block = [];
            }

            if (block.Count == 0)
            {
                start = tag.Address;
            }

            block.Add(tag);
            end = Math.Max(end, nextEnd);
        }

        if (block.Count > 0)
        {
            yield return block;
        }
    }

    private async Task FlushAlarmsAsync()
    {
        while (pendingAlarms.TryPeek(out var alarm))
        {
            var rule = rules[alarm.RuleId];
            if (alarm.Transition == AlarmTransition.Raised)
            {
                await repository.RaiseAlarmAsync(
                    new()
                    {
                        Id = alarm.OccurrenceId.ToString(),
                        RuleId = alarm.RuleId,
                        TagId = rule.TagId,
                        Name = tags.FirstOrDefault(t => t.Id == rule.TagId)?.Name ?? rule.TagId,
                        Severity = alarm.Severity.ToString(),
                        RaisedAt = alarm.Timestamp.ToUnixTimeMilliseconds(),
                        Value = alarm.Value
                    }
                );
            }
            else if (alarm.Transition == AlarmTransition.Recovered)
            {
                await repository.RecoverAlarmAsync(alarm.OccurrenceId.ToString(), alarm.Timestamp
                    .ToUnixTimeMilliseconds());
            }

            logger.LogWarning(
                "Alarm {Rule} {Transition} occurrence {Id}",
                alarm.RuleId,
                alarm.Transition,
                alarm.OccurrenceId
            );
            pendingAlarms.Dequeue();
        }
    }

    public async Task ControlAsync(
        int pump,
        bool run,
        string mode,
        CancellationToken token = default
    )
    {
        if (!settings.PumpControls.ContainsKey(pump))
        {
            throw new ArgumentOutOfRangeException(nameof(pump));
        }

        await gate.WaitAsync(token);
        try
        {
            if (client is null)
            {
                throw new InvalidOperationException("采集服务尚未就绪，请稍后重试");
            }

            var control = settings.PumpControls[pump];
            var device = devices.Single(item => item.Id == control.DeviceId);
            byte unit = (byte)device.UnitId;
            ushort registerValue = run ? control.StartValue : control.StopValue;
            if (mode == "Register")
            {
                await client!.WriteRegisterAsync(unit, control.RegisterAddress, registerValue, token);
            }
            else
            {
                await client!.WriteCoilAsync(unit, control.CoilAddress, run, token);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
