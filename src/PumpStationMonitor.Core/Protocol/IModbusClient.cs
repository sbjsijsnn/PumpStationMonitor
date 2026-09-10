namespace PumpStationMonitor.Core.Protocol;

public enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting }

public sealed record ModbusOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);
    public int Retries { get; init; } = 2;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);
}

public interface IModbusClient : IAsyncDisposable
{
    ConnectionState State { get; }
    event Action<ConnectionState>? StateChanged;
    Task<bool[]> ReadBitsAsync(byte unit, byte function, ushort address, ushort count, CancellationToken cancellationToken = default);
    Task<ushort[]> ReadRegistersAsync(byte unit, byte function, ushort address, ushort count, CancellationToken cancellationToken = default);
    Task WriteCoilAsync(byte unit, ushort address, bool value, CancellationToken cancellationToken = default);
    Task WriteRegisterAsync(byte unit, ushort address, ushort value, CancellationToken cancellationToken = default);
    Task WriteRegistersAsync(byte unit, ushort address, ushort[] values, CancellationToken cancellationToken = default);
}
