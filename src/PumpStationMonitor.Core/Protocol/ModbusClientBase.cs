using Microsoft.Extensions.Logging;

namespace PumpStationMonitor.Core.Protocol;

// 实现 Modbus 客户端共用的串行请求、超时和重连策略。
// 传输差异留给 TCP 与 RTU 子类，协议校验和状态事件保持一致。
// 写入应答丢失时不自动重发，防止重复控制设备。
public abstract class ModbusClientBase : IModbusClient
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ModbusOptions options;
    protected readonly ILogger Logger;
    private bool disposed;
    public ConnectionState State
    {
        get;
        private set;
    }

    public event Action<ConnectionState>? StateChanged;
    protected ModbusClientBase(ModbusOptions options, ILogger logger)
    {
        if (options.Timeout <= TimeSpan.Zero || options.Retries is < 0 or > 10
                || options.RetryDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        this.options = options;
        Logger = logger;
    }

    protected abstract Task ConnectAsync(CancellationToken token);
    protected abstract Task<byte[]> ExchangeAsync(byte unit, byte[] pdu, CancellationToken token);
    protected abstract void Disconnect();
    private void SetState(ConnectionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    private async Task<byte[]> ExecuteAsync(byte unit, byte[] request, CancellationToken token)
    {
        // 先验证站号，再独占通信通道；请求与响应必须成对完成。
        ModbusFrames.ValidateUnit(unit);
        await gate.WaitAsync(token);
        try
        {
            // 持锁后检查释放状态，防止等待期间客户端已被关闭。
            ObjectDisposedException.ThrowIf(disposed, this);
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                // 每次尝试独立计时，同时响应调用者取消。
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(options.Timeout);
                try
                {
                    if (State != ConnectionState.Connected)
                    {
                        SetState(attempt == 0
                                ? ConnectionState.Connecting
                                : ConnectionState.Reconnecting);
                    }

                    // 连接后收发并验证应答，只有通过校验才发布已连接状态。
                    await ConnectAsync(timeout.Token);
                    var response = await ExchangeAsync(unit, request, timeout.Token);
                    ModbusFrames.ValidateResponse(request, response);
                    SetState(ConnectionState.Connected);
                    return response;
                }
                catch (ModbusException ex)
                {
                    // 设备异常应答说明链路仍然畅通；直接上抛业务拒绝，不重连重试。
                    Logger.LogError(ex, "Device rejected request");
                    SetState(ConnectionState.Connected);
                    throw;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException
                    or System.Net.Sockets.SocketException or OperationCanceledException
                    or TimeoutException or UnauthorizedAccessException)
                {
                    // 通信失败先清理连接；调用者取消与请求超时分别处理。
                    Disconnect();
                    SetState(ConnectionState.Disconnected);
                    Logger.LogError(ex, "Communication failed on attempt {Attempt}", attempt + 1);
                    token.ThrowIfCancellationRequested();
                    // 写请求可能已执行但应答丢失，禁止自动重发控制命令。
                     if (attempt >= options.Retries || request[0] is 5 or 6 or 16)
                    {
                        if (ex is OperationCanceledException)
                        {
                            throw new TimeoutException("Modbus response timed out", ex);
                        }

                        throw;
                    }

                    // 只有可重试读请求到达此处，指数退避并设置等待上限。
                    SetState(ConnectionState.Reconnecting);
                    double exponentialDelay = options.RetryDelay.TotalMilliseconds * Math.Pow(2, attempt);
                    double boundedDelay = Math.Min(10000, exponentialDelay);
                    var retryDelay = TimeSpan.FromMilliseconds(boundedDelay);
                    await Task.Delay(retryDelay, token);
                }
            }
        }
        finally
        {
            // 退出时收束未完成的连接状态，并确保释放通信锁。
            if (State is ConnectionState.Connecting or ConnectionState.Reconnecting)
            {
                SetState(ConnectionState.Disconnected);
            }

            gate.Release();
        }
    }

    public async Task<bool[]> ReadBitsAsync(
        byte unit,
        byte function,
        ushort address,
        ushort count,
        CancellationToken cancellationToken = default
    )
    {
        if (function is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(function));
        }

        ModbusFrames.ValidateRange(address, count, 2000);
        var response = await ExecuteAsync(unit, ModbusFrames.Request(function, address, count), cancellationToken);
        return Enumerable.Range(0, count).Select(i => (response[2 + i / 8] & (1 << (i % 8))) != 0).ToArray();
    }

    public async Task<ushort[]> ReadRegistersAsync(
        byte unit,
        byte function,
        ushort address,
        ushort count,
        CancellationToken cancellationToken = default
    )
    {
        if (function is not (3 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(function));
        }

        ModbusFrames.ValidateRange(address, count, 125);
        var response = await ExecuteAsync(unit, ModbusFrames.Request(function, address, count), cancellationToken);
        return Enumerable.Range(0, count).Select(i => ModbusFrames.ReadUInt16(response.AsSpan(2 + i * 2))).ToArray();
    }

    public async Task WriteCoilAsync(
        byte unit,
        ushort address,
        bool value,
        CancellationToken cancellationToken = default
    ) => _ = await ExecuteAsync(
        unit,
        ModbusFrames.Request(5, address, value ? (ushort)0xFF00 : (ushort)0),
        cancellationToken
    );
    public async Task WriteRegisterAsync(
        byte unit,
        ushort address,
        ushort value,
        CancellationToken cancellationToken = default
    ) => _ = await ExecuteAsync(unit, ModbusFrames.Request(6, address, value), cancellationToken);
    public async Task WriteRegistersAsync(
        byte unit,
        ushort address,
        ushort[] values,
        CancellationToken cancellationToken = default
    ) => _ = await ExecuteAsync(unit, ModbusFrames.WriteMultiple(address, values), cancellationToken);
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Disconnect();
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            gate.Release();
        }

        GC.SuppressFinalize(this);
    }
}
