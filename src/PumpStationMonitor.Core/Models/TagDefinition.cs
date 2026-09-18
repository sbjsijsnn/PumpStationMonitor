using System.Buffers.Binary;

namespace PumpStationMonitor.Core.Models;

public enum RegisterOrder
{
    Abcd,
    Badc,
    Cdab,
    Dcba
}

public enum DataQuality
{
    Good,
    Bad,
    Stale
}

// 描述设备标识、名称和 Modbus 站号。
// 设备寻址信息独立于点位，避免每个点位重复维护站号。
// 不可变记录适合在协议示例与业务层之间传递。
public sealed record DeviceDefinition(string Id, string Name, byte UnitId);
// 描述点位读取地址、数据类型和工程量换算参数。
// 将定义与采样值分开，使配置不会随每轮采集重复变化。
// 不可变记录便于读取与传递，质量由采样单独表达。
public sealed record TagDefinition(
    string Id,
    string DeviceId,
    string Name,
    byte Function,
    ushort Address,
    string Unit,
    double Scale = 1,
    double Offset = 0,
    int? Bit = null
);
// 表示一个点位在某个时刻的数值和数据质量。
// 用不可变记录一起传递时间与质量，避免报警只收到裸数值。
// 报警引擎可用同样的数据结构处理实时采样与确定性测试。
public sealed record TagSample(
    string TagId,
    double Value,
    DataQuality Quality,
    DateTimeOffset Timestamp
);
// 提供寄存器位提取、浮点字节序和工程量换算。
// 纯函数不依赖连接状态，便于独立测试不同设备编码方式。
// 解码与缩放分开，让点表中的比例和偏移保持明确。
public static class RegisterConverter
{
    public static double Scale(double raw, double scale, double offset) => raw * scale + offset;
    public static bool Bit(ushort word, int bit) => bit is >= 0 and < 16
        ? (word & (1 << bit)) != 0
        : throw new ArgumentOutOfRangeException(nameof(bit));
    public static float Float32(ushort first, ushort second, RegisterOrder order)
    {
        byte[] bytes = [(byte)(first >> 8), (byte)first, (byte)(second >> 8), (byte)second];
        byte[] ordered = order switch
        {
            RegisterOrder.Abcd => bytes,
            RegisterOrder.Badc => [bytes[1], bytes[0], bytes[3], bytes[2]],
            RegisterOrder.Cdab => [bytes[2], bytes[3], bytes[0], bytes[1]],
            RegisterOrder.Dcba => [bytes[3], bytes[2], bytes[1], bytes[0]],
            _ => throw new ArgumentOutOfRangeException(nameof(order))
        };
        return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(ordered));
    }
}
