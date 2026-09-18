using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace PumpStationMonitor.Core.Protocol;

// 描述串口名称、波特率、校验和停止位。
// 使用不可变记录完整传递串口参数，避免构造时遗漏字段。
// 串口传输实现读取此对象，业务层无需创建串口连接。
public sealed record SerialSettings(
    string PortName,
    int BaudRate = 9600,
    Parity Parity = Parity.Even,
    int DataBits = 8,
    StopBits StopBits = StopBits.One
);
// 通过串口传输 Modbus RTU 请求与响应。
// 把串口参数和帧读取集中封装，避免业务层接触串口对象。
// 超时与重连复用公共客户端策略，RTU 层专注校验和传输。
public sealed class ModbusRtuClient(
    SerialSettings settings,
    ModbusOptions options,
    ILogger<ModbusRtuClient> logger
) : ModbusClientBase(options, logger)
{
    private SerialPort? port;
    protected override async Task ConnectAsync(CancellationToken token)
    {
        if (port is not null)
        {
            return;
        }

        port = new SerialPort(
            settings.PortName,
            settings.BaudRate,
            settings.Parity,
            settings.DataBits,
            settings.StopBits
        );
        // SerialPort 没有 OpenAsync，在线程池打开，避免阻塞 UI 线程。
        await Task.Run(port.Open, token);
    }

    protected override async Task<byte[]> ExchangeAsync(byte unit, byte[] pdu, CancellationToken token)
    {
        var serial = port!;
        // Windows 串口驱动不一定支持取消 ReadAsync；超时关闭端口以终止挂起读取。
        using var registration = token.Register(
            () =>
            {
                try
                {
                    serial.Close();
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to close cancelled serial request");
                }
            }
        );
        try
        {
            double bits = 1 + settings.DataBits + (settings.Parity == Parity.None ? 0 : 1)
                + (settings.StopBits == StopBits.Two ? 2 : 1);
            await Task.Delay(
                TimeSpan.FromMilliseconds(
                    settings.BaudRate > 19200
                        ? 2
                        : Math.Ceiling(3500 * bits / settings.BaudRate)
                ),
                token
            );
            serial.DiscardInBuffer();
            var request = ModbusFrames.Rtu(unit, pdu);
            Logger.LogDebug("TX RTU {Frame}", Convert.ToHexString(request));
            await serial.BaseStream.WriteAsync(request, token);
            byte[] prefix = new byte[3];
            await serial.BaseStream.ReadExactlyAsync(prefix, token);
            int length = (prefix[1] & 0x80) != 0
                ? 5
                : prefix[1] is 1 or 2 or 3 or 4 ? prefix[2] + 5 : 8;
            if (length > 256)
            {
                throw new InvalidDataException("Invalid RTU length");
            }

            byte[] frame = new byte[length];
            prefix.CopyTo(frame, 0);
            await serial.BaseStream.ReadExactlyAsync(frame.AsMemory(3), token);
            Logger.LogDebug("RX RTU {Frame}", Convert.ToHexString(frame));
            return ModbusFrames.ParseRtu(frame, unit);
        }
        catch (Exception ex) when (token.IsCancellationRequested)
        {
            Logger.LogError(ex, "Serial request cancelled or timed out");
            throw new OperationCanceledException("Serial request cancelled", ex, token);
        }
    }

    protected override void Disconnect()
    {
        port?.Dispose();
        port = null;
    }
}
