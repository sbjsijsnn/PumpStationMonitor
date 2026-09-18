using System.Buffers.Binary;

namespace PumpStationMonitor.Core.Protocol;

// 表达设备返回的 Modbus 异常应答。
// 将功能码和异常码保留为结构化信息，便于日志与调用方识别。
// 与运输层失败区分，使客户端不会为业务拒绝错误重连。
public sealed class ModbusException(byte function, byte code)
    : Exception($"Modbus function 0x{function:X2} failed: exception 0x{code:X2}")
{
    public byte Code { get; } = code;
}

// 集中构造和验证 Modbus PDU、TCP 与 RTU 帧。
// 把纯字节规则从连接代码抽出，便于使用已知报文验证边界。
// 请求范围与响应回显统一校验，避免各传输实现规则漂移。
public static class ModbusFrames
{
    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }
        }

        return crc;
    }

    public static ushort ReadUInt16(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt16BigEndian(bytes);
    public static void WriteUInt16(Span<byte> bytes, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(
        bytes, value);
    public static byte[] Request(byte function, ushort address, ushort value)
    {
        byte[] pdu = new byte[5];
        pdu[0] = function;
        WriteUInt16(pdu.AsSpan(1), address);
        WriteUInt16(pdu.AsSpan(3), value);
        return pdu;
    }

    public static byte[] WriteMultiple(ushort address, ReadOnlySpan<ushort> values)
    {
        ValidateRange(address, values.Length, 123);
        var pdu = new byte[6 + values.Length * 2];
        Request(16, address, (ushort)values.Length).CopyTo(pdu, 0);
        pdu[5] = (byte)(values.Length * 2);
        for (int i = 0; i < values.Length; i++)
        {
            WriteUInt16(pdu.AsSpan(6 + i * 2), values[i]);
        }

        return pdu;
    }

    public static void ValidateRange(ushort address, int count, int maximum)
    {
        if (count < 1 || count > maximum || address + count > 65536)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
    }

    public static byte[] Rtu(byte unit, ReadOnlySpan<byte> pdu)
    {
        ValidateUnit(unit);
        if (pdu.Length is < 1 or > 253)
        {
            throw new ArgumentException("Invalid PDU length");
        }

        byte[] frame = new byte[pdu.Length + 3];
        frame[0] = unit;
        pdu.CopyTo(frame.AsSpan(1));
        // CRC 低字节先发，与寄存器的大端编码不同。
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - 2), Crc16(frame.AsSpan(0, frame
                    .Length - 2)));
        return frame;
    }

    public static byte[] ParseRtu(ReadOnlySpan<byte> frame, byte unit)
    {
        if (frame.Length is < 5 or > 256 || frame[0] != unit
                || Crc16(frame[..^2]) != BinaryPrimitives.ReadUInt16LittleEndian(frame[^2..]))
        {
            throw new InvalidDataException("Invalid RTU length, unit or CRC");
        }

        return frame[1..^2].ToArray();
    }

    public static byte[] Tcp(ushort transaction, byte unit, ReadOnlySpan<byte> pdu)
    {
        ValidateUnit(unit);
        if (pdu.Length is < 1 or > 253)
        {
            throw new ArgumentException("Invalid PDU length");
        }

        byte[] frame = new byte[7 + pdu.Length];
        WriteUInt16(frame, transaction);
        WriteUInt16(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = unit;
        pdu.CopyTo(frame.AsSpan(7));
        return frame;
    }

    public static byte[] ParseTcp(ReadOnlySpan<byte> frame, ushort transaction, byte unit)
    {
        if (frame.Length is < 9 or > 260 || ReadUInt16(frame) != transaction
                || ReadUInt16(frame[2..]) != 0
                || ReadUInt16(frame[4..]) != frame.Length - 6
                || frame[6] != unit)
        {
            throw new InvalidDataException("Invalid MBAP header or length");
        }

        return frame[7..].ToArray();
    }

    public static void ValidateUnit(byte unit)
    {
        if (unit is < 1 or > 247)
        {
            throw new ArgumentOutOfRangeException(nameof(unit), "Only addressed devices 1..247 are supported");
        }
    }

    public static void ValidateResponse(ReadOnlySpan<byte> request, ReadOnlySpan<byte> response)
    {
        if (response.Length == 2 && response[0] == (request[0] | 0x80))
        {
            throw new ModbusException(request[0], response[1]);
        }

        if (response.Length < 2 || response[0] != request[0])
        {
            throw new InvalidDataException("Unexpected function");
        }

        if (request[0] is 1 or 2 or 3 or 4)
        {
            int quantity = ReadUInt16(request[3..]);
            int expected = request[0] is 1 or 2 ? (quantity + 7) / 8 : quantity * 2;
            if (response[1] != expected || response.Length != expected + 2)
            {
                throw new InvalidDataException("Unexpected byte count");
            }
        }
        else if (response.Length != 5 || !response.SequenceEqual(request[..5]))
        {
            throw new InvalidDataException("Write acknowledgement does not match request");
        }
    }
}
