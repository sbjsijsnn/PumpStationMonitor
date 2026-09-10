using Microsoft.Extensions.Logging;

namespace PumpStationMonitor.Core.Protocol;

public abstract class ModbusClientBase : IModbusClient
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ModbusOptions options;
    protected readonly ILogger Logger;
    private bool disposed;
    public ConnectionState State { get; private set; }
    public event Action<ConnectionState>? StateChanged;

    protected ModbusClientBase(ModbusOptions options, ILogger logger)
    {
        if (options.Timeout <= TimeSpan.Zero || options.Retries is < 0 or > 10 || options.RetryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        this.options = options;
        Logger = logger;
    }

    protected abstract Task ConnectAsync(CancellationToken token);
    protected abstract Task<byte[]> ExchangeAsync(byte unit, byte[] pdu, CancellationToken token);
    protected abstract void Disconnect();

    private void SetState(ConnectionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private async Task<byte[]> ExecuteAsync(byte unit, byte[] request, CancellationToken token)
    {
        ModbusFrames.ValidateUnit(unit);
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(options.Timeout);
                try
                {
                    if (State != ConnectionState.Connected) SetState(attempt == 0 ? ConnectionState.Connecting : ConnectionState.Reconnecting);
                    await ConnectAsync(timeout.Token);
                    var response = await ExchangeAsync(unit, request, timeout.Token);
                    ModbusFrames.ValidateResponse(request, response);
                    SetState(ConnectionState.Connected);
                    return response;
                }
                catch (ModbusException ex)
                {
                    Logger.LogError(ex, "Device rejected request");
                    SetState(ConnectionState.Connected);
                    throw;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or System.Net.Sockets.SocketException or OperationCanceledException or TimeoutException or UnauthorizedAccessException)
                {
                    Disconnect();
                    SetState(ConnectionState.Disconnected);
                    Logger.LogError(ex, "Communication failed on attempt {Attempt}", attempt + 1);
                    token.ThrowIfCancellationRequested();
                    // 写请求可能已执行但应答丢失，禁止自动重发控制命令。
                    if (attempt >= options.Retries || request[0] is 5 or 6 or 16)
                    {
                        if (ex is OperationCanceledException) throw new TimeoutException("Modbus response timed out", ex);
                        throw;
                    }
                    SetState(ConnectionState.Reconnecting);
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10000, options.RetryDelay.TotalMilliseconds * Math.Pow(2, attempt))), token);
                }
            }
        }
        finally
        {
            if (State is ConnectionState.Connecting or ConnectionState.Reconnecting) SetState(ConnectionState.Disconnected);
            gate.Release();
        }
    }

    public async Task<bool[]> ReadBitsAsync(byte unit, byte function, ushort address, ushort count, CancellationToken cancellationToken = default)
    {
        if (function is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(function));
        ModbusFrames.ValidateRange(address, count, 2000);
        var response = await ExecuteAsync(unit, ModbusFrames.Request(function, address, count), cancellationToken);
        return Enumerable.Range(0, count).Select(i => (response[2 + i / 8] & (1 << (i % 8))) != 0).ToArray();
    }

    public async Task<ushort[]> ReadRegistersAsync(byte unit, byte function, ushort address, ushort count, CancellationToken cancellationToken = default)
    {
        if (function is not (3 or 4)) throw new ArgumentOutOfRangeException(nameof(function));
        ModbusFrames.ValidateRange(address, count, 125);
        var response = await ExecuteAsync(unit, ModbusFrames.Request(function, address, count), cancellationToken);
        return Enumerable.Range(0, count).Select(i => ModbusFrames.ReadUInt16(response.AsSpan(2 + i * 2))).ToArray();
    }

    public async Task WriteCoilAsync(byte unit, ushort address, bool value, CancellationToken cancellationToken = default) =>
        _ = await ExecuteAsync(unit, ModbusFrames.Request(5, address, value ? (ushort)0xFF00 : (ushort)0), cancellationToken);

    public async Task WriteRegisterAsync(byte unit, ushort address, ushort value, CancellationToken cancellationToken = default) =>
        _ = await ExecuteAsync(unit, ModbusFrames.Request(6, address, value), cancellationToken);

    public async Task WriteRegistersAsync(byte unit, ushort address, ushort[] values, CancellationToken cancellationToken = default) =>
        _ = await ExecuteAsync(unit, ModbusFrames.WriteMultiple(address, values), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            Disconnect();
            SetState(ConnectionState.Disconnected);
        }
        finally { gate.Release(); }
        GC.SuppressFinalize(this);
    }
}
