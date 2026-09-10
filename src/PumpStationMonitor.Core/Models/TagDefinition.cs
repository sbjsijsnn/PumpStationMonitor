using System.Buffers.Binary;

namespace PumpStationMonitor.Core.Models;

public enum RegisterOrder { Abcd, Badc, Cdab, Dcba }
public enum DataQuality { Good, Bad, Stale }
public sealed record DeviceDefinition(string Id, string Name, byte UnitId);
public sealed record TagDefinition(string Id, string DeviceId, string Name, byte Function, ushort Address, string Unit, double Scale = 1, double Offset = 0, int? Bit = null);
public sealed record TagSample(string TagId, double Value, DataQuality Quality, DateTimeOffset Timestamp);

public static class RegisterConverter
{
    public static double Scale(double raw, double scale, double offset) => raw * scale + offset;
    public static bool Bit(ushort word, int bit) => bit is >= 0 and < 16 ? (word & (1 << bit)) != 0 : throw new ArgumentOutOfRangeException(nameof(bit));

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
