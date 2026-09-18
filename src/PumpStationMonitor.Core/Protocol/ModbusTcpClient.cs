using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace PumpStationMonitor.Core.Protocol;

// 通过 TCP 连接传输 Modbus 请求并关联响应。
// 按 MBAP 长度读取完整报文，处理网络分包与连续帧。
// 连接生命周期交给公共客户端策略，传输层只处理字节收发。
public sealed class ModbusTcpClient(
    string host,
    int port,
    ModbusOptions options,
    ILogger<ModbusTcpClient> logger
) : ModbusClientBase(options, logger)
{
    private TcpClient? client;
    private ushort transaction;
    protected override async Task ConnectAsync(CancellationToken token)
    {
        if (client is not null)
        {
            return;
        }

        client = new TcpClient
        {
            NoDelay = true
        };
        await client.ConnectAsync(host, port, token);
    }

    protected override async Task<byte[]> ExchangeAsync(byte unit, byte[] pdu, CancellationToken token)
    {
        var id = unchecked(++transaction);
        var request = ModbusFrames.Tcp(id, unit, pdu);
        var stream = client!.GetStream();
        Logger.LogDebug("TX TCP {Frame}", Convert.ToHexString(request));
        await stream.WriteAsync(request, token);
        // TCP 是字节流：先读完整头部，再按 MBAP 的长度读完整正文。
        byte[] header = new byte[7];
        await stream.ReadExactlyAsync(header, token);
        int length = ModbusFrames.ReadUInt16(header.AsSpan(4));
        if (length is < 3 or > 254)
        {
            throw new InvalidDataException("Invalid MBAP length");
        }

        byte[] frame = new byte[6 + length];
        header.CopyTo(frame, 0);
        await stream.ReadExactlyAsync(frame.AsMemory(7), token);
        Logger.LogDebug("RX TCP {Frame}", Convert.ToHexString(frame));
        return ModbusFrames.ParseTcp(frame, id, unit);
    }

    protected override void Disconnect()
    {
        client?.Dispose();
        client = null;
    }
}
