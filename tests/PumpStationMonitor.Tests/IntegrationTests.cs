using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using PumpStationMonitor.Core.Protocol;
using PumpStationMonitor.Simulator;

namespace PumpStationMonitor.Tests;

// 验证通信集成的既有行为和边界条件。
// 使用可重复的输入与断言，确保重构后外部行为保持一致。
// 测试数据与运行状态隔离，便于单独执行和定位回归。
public class IntegrationTests
{
    private static ModbusTcpClient Client(int port, int retries = 0) => new(
        "127.0.0.1",
        port,
        new ModbusOptions
        {
            Timeout = TimeSpan.FromMilliseconds(300),
            Retries = retries,
            RetryDelay = TimeSpan.FromMilliseconds(10)
        },
        NullLogger<ModbusTcpClient>.Instance
    );
    [Fact]
    public async Task AllFunctionsAndFaultInjection()
    {
        var station = new StationState();
        await using var server = new ModbusServer(station, NullLogger<ModbusServer>.Instance);
        server.Start(0);
        await using var client = Client(server.Port);
        await client.WriteRegisterAsync(1, 100, 1234);
        Assert.Equal((ushort)1234, (await client.ReadRegistersAsync(1, 3, 100, 1))[0]);
        await client.WriteRegistersAsync(1, 100, [11, 22, 33]);
        Assert.Equal(
            new ushort[] { 11, 22, 33 },
            await client.ReadRegistersAsync(1, 3, 100, 3)
        );
        await client.WriteCoilAsync(1, 0, true);
        Assert.True((await client.ReadBitsAsync(1, 1, 0, 9))[0]);
        station.Command("fault 1");
        station.Command("level 9.5");
        station.Update(0);
        Assert.Equal(
            new[] { true, false, true, false },
            await client.ReadBitsAsync(1, 2, 0, 4)
        );
        Assert.Equal((ushort)950, (await client.ReadRegistersAsync(1, 4, 20, 1))[0]);
        Assert.Equal((ushort)14, (await client.ReadRegistersAsync(1, 3, 0, 1))[0]);
        station.Command("recover");
        station.Update(0);
        Assert.False((await client.ReadBitsAsync(1, 2, 0, 1))[0]);
        await client.WriteRegisterAsync(1, 40, 0);
        Assert.False((await client.ReadBitsAsync(1, 1, 0, 1))[0]);
        var ex = await Assert.ThrowsAsync<ModbusException>(() => client.ReadRegistersAsync(1, 3, 127, 2));
        Assert.Equal((byte)2, ex.Code);
    }

    [Fact]
    public async Task TimeoutAndNextRequestReconnects()
    {
        var station = new StationState();
        await using var server = new ModbusServer(station, NullLogger<ModbusServer>.Instance);
        server.Start(0);
        await using var client = Client(server.Port, 1);
        var states = new List<ConnectionState>();
        client.StateChanged += states.Add;
        station.Command("offline");
        await Assert.ThrowsAsync<TimeoutException>(() => client.ReadRegistersAsync(1, 3, 20, 1));
        Assert.Contains(ConnectionState.Reconnecting, states);
        Assert.Equal(ConnectionState.Disconnected, client.State);
        station.Command("recover");
        Assert.Single(await client.ReadRegistersAsync(1, 3, 20, 1));
        Assert.Equal(ConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task CancelPendingReadAndReuseClient()
    {
        var station = new StationState();
        await using var server = new ModbusServer(station, NullLogger<ModbusServer>.Instance);
        server.Start(0);
        await using var client = Client(server.Port);
        station.Command("offline");
        using var cancel = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadRegistersAsync(1, 3, 0, 1,
                cancel.Token));
        station.Command("recover");
        Assert.Single(await client.ReadRegistersAsync(1, 3, 0, 1));
    }

    [Fact]
    public async Task ConcurrentRequestsDoNotMixTransactions()
    {
        var station = new StationState();
        await using var server = new ModbusServer(station, NullLogger<ModbusServer>.Instance);
        server.Start(0);
        await using var client = Client(server.Port);
        await Task.WhenAll(
            Enumerable.Range(0, 20).Select(
                async i =>
                {
                    await client.WriteRegisterAsync(1, (ushort)(100 + i), (ushort)i);
                    Assert.Equal(
                        (ushort)i,
                        (await client.ReadRegistersAsync(1, 3, (ushort)(100 + i), 1))[0]
                    );
                }
            )
        );
    }

    [Fact]
    public async Task ClientHandlesFragmentedResponse()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(
            async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync();
                var stream = socket.GetStream();
                byte[] request = new byte[12];
                await stream.ReadExactlyAsync(request);
                var response = ModbusFrames.Tcp(ModbusFrames.ReadUInt16(request), 1, [3, 2, 0x12, 0x34]);
                foreach (var b in response)
                {
                    await stream.WriteAsync(new byte[] { b });
                    await Task.Delay(2);
                }
            }
        );
        await using var client = Client(((IPEndPoint)listener.LocalEndpoint).Port);
        Assert.Equal((ushort)0x1234, (await client.ReadRegistersAsync(1, 3, 0, 1))[0]);
        await server;
    }

    [Fact]
    public async Task ServerHandlesCoalescedRequests()
    {
        var station = new StationState();
        await using var server = new ModbusServer(station, NullLogger<ModbusServer>.Instance);
        server.Start(0);
        using var socket = new TcpClient();
        await socket.ConnectAsync("127.0.0.1", server.Port);
        var stream = socket.GetStream();
        byte[] a = ModbusFrames.Tcp(1, 1, ModbusFrames.Request(3, 20, 1));
        byte[] b = ModbusFrames.Tcp(2, 1, ModbusFrames.Request(3, 20, 1));
        await stream.WriteAsync(a.Concat(b).ToArray());
        byte[] response = new byte[22];
        using var cancel = new CancellationTokenSource(2000);
        await stream.ReadExactlyAsync(response, cancel.Token);
        Assert.Equal((byte)3, ModbusFrames.ParseTcp(response.AsSpan(0, 11), 1, 1)[0]);
        Assert.Equal((byte)3, ModbusFrames.ParseTcp(response.AsSpan(11), 2, 1)[0]);
    }

    [Fact]
    public async Task LostWriteAcknowledgementIsNotRetried()
    {
        var station = new StationState();
        await using var server = new ModbusServer(station, NullLogger<ModbusServer>.Instance);
        server.Start(0);
        await using var client = Client(server.Port, 3);
        var states = new List<ConnectionState>();
        client.StateChanged += states.Add;
        station.Command("offline");
        await Assert.ThrowsAsync<TimeoutException>(() => client.WriteRegisterAsync(1, 100, 1));
        Assert.DoesNotContain(ConnectionState.Reconnecting, states);
    }

    [Fact]
    public async Task MalformedResponseClosesConnectionAndRetriesRead()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(3000);
        var server = Task.Run(
            async () =>
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
                    var stream = socket.GetStream();
                    byte[] request = new byte[12];
                    await stream.ReadExactlyAsync(request, deadline.Token);
                    var response = ModbusFrames.Tcp(ModbusFrames.ReadUInt16(request), 1, [3, 2, 0, 42]);
                    if (attempt == 0)
                    {
                        response[1] ^= 1;
                    }

                    await stream.WriteAsync(response, deadline.Token);
                }
            }
        );
        await using var client = Client(((IPEndPoint)listener.LocalEndpoint).Port, 1);
        Assert.Equal(
            (ushort)42,
            (await client.ReadRegistersAsync(1, 3, 0, 1, deadline.Token))[0]
        );
        await server;
    }
}
