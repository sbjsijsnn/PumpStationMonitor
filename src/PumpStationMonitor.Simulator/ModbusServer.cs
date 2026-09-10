using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using PumpStationMonitor.Core.Protocol;
namespace PumpStationMonitor.Simulator;
public sealed class ModbusServer(StationState station, ILogger<ModbusServer> logger) : IAsyncDisposable
{
    private TcpListener? listener;
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<int, Task> clients = new();
    private Task? acceptLoop;
    private Task? updateLoop;
    private int nextId;
    public int Port => ((IPEndPoint)listener!.LocalEndpoint).Port;
    public void Start(int port = 1502)
    {
        if (listener is not null) throw new InvalidOperationException("Already started");
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        station.Update(0);
        acceptLoop = AcceptAsync(stopping.Token);
        updateLoop = UpdateAsync(stopping.Token);
    }
    private async Task UpdateAsync(CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try { while (await timer.WaitForNextTickAsync(token)) station.Update(watch.Elapsed.TotalSeconds); }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested) { logger.LogDebug(ex, "Simulation stopped"); }
    }
    private async Task AcceptAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener!.AcceptTcpClientAsync(token);
                int id = Interlocked.Increment(ref nextId);
                clients[id] = ServeAsync(client, token);
                // 及时释放已完成的连接任务，避免长期积累。
                foreach (var pair in clients.Where(p => p.Value.IsCompleted))
                    if (clients.TryRemove(pair.Key, out var completed)) await completed;
            }
        }
        catch (OperationCanceledException ex) when (token.IsCancellationRequested) { logger.LogDebug(ex, "Listener stopped"); }
    }
    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                while (!token.IsCancellationRequested)
                {
                    byte[] header = new byte[7];
                    await stream.ReadExactlyAsync(header, token);
                    int length = ModbusFrames.ReadUInt16(header.AsSpan(4));
                    if (length is < 2 or > 254 || ModbusFrames.ReadUInt16(header.AsSpan(2)) != 0) throw new InvalidDataException("Invalid request MBAP");
                    var pdu = new byte[length - 1];
                    await stream.ReadExactlyAsync(pdu, token);
                    logger.LogDebug("RX {Header} {Pdu}", Convert.ToHexString(header), Convert.ToHexString(pdu));
                    var response = station.Respond(header[6], pdu);
                    if (response is not null)
                    {
                        var frame = ModbusFrames.Tcp(ModbusFrames.ReadUInt16(header), header[6], response);
                        logger.LogDebug("TX {Frame}", Convert.ToHexString(frame));
                        await stream.WriteAsync(frame, token);
                    }
                }
            }
            catch (OperationCanceledException ex) when (token.IsCancellationRequested) { logger.LogDebug(ex, "Client stopped"); }
            catch (EndOfStreamException ex) { logger.LogDebug(ex, "Peer disconnected"); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException) { logger.LogError(ex, "Client connection failed"); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        if (acceptLoop is not null) await acceptLoop;
        listener?.Stop();
        if (updateLoop is not null) await updateLoop;
        await Task.WhenAll(clients.Values);
        stopping.Dispose();
    }
}
