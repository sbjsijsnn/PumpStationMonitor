namespace PumpStationMonitor.Core.Protocol;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

// 保存请求超时、重试次数和退避参数。
// 使用独立配置对象，让 TCP 与 RTU 使用相同的请求策略。
// 默认值服务于本地站点，构造客户端时校验有效范围。
public sealed record ModbusOptions
{
    public TimeSpan Timeout
    {
        get;
        init;
    } = TimeSpan.FromSeconds(2);
    public int Retries
    {
        get;
        init;
    } = 2;
    public TimeSpan RetryDelay
    {
        get;
        init;
    } = TimeSpan.FromMilliseconds(200);
}

public interface IModbusClient : IAsyncDisposable
{
    ConnectionState State
    {
        get;
    }

    event Action<ConnectionState>? StateChanged;
    Task<bool[]> ReadBitsAsync(
        byte unit,
        byte function,
        ushort address,
        ushort count,
        CancellationToken cancellationToken = default
    );
    Task<ushort[]> ReadRegistersAsync(
        byte unit,
        byte function,
        ushort address,
        ushort count,
        CancellationToken cancellationToken = default
    );
    Task WriteCoilAsync(
        byte unit,
        ushort address,
        bool value,
        CancellationToken cancellationToken = default
    );
    Task WriteRegisterAsync(
        byte unit,
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default
    );
    Task WriteRegistersAsync(
        byte unit,
        ushort address,
        ushort[] values,
        CancellationToken cancellationToken = default
    );
}
