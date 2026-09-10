using PumpStationMonitor.Core.Models;
using PumpStationMonitor.Core.Protocol;
namespace PumpStationMonitor.Core.Services;

public sealed class DeviceRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int UnitId { get; set; } = 1;
}
public sealed class TagRow
{
    public string Id { get; set; } = "";
    public string DeviceId { get; set; } = "pump1";
    public string Name { get; set; } = "新点位";
    public int Function { get; set; } = 3;
    public int Address { get; set; }
    public string Unit { get; set; } = "";
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }
    public int? Bit { get; set; }
    public string DataType { get; set; } = "UInt16";
    public string ByteOrder { get; set; } = "Abcd";
    public bool Enabled { get; set; } = true;
}
public sealed class RuleRow
{
    public string Id { get; set; } = "";
    public string TagId { get; set; } = "level";
    public string Kind { get; set; } = "H";
    public double LimitValue { get; set; } = 8;
    public double Deadband { get; set; } = 0.3;
    public int DelayMs { get; set; } = 1000;
    public string Severity { get; set; } = "Warning";
    public bool Enabled { get; set; } = true;
}
public sealed class AlarmRow
{
    public string Id { get; set; } = "";
    public string RuleId { get; set; } = "";
    public string TagId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Severity { get; set; } = "";
    public long RaisedAt { get; set; }
    public long? RecoveredAt { get; set; }
    public long? AcknowledgedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public double Value { get; set; }
    public string Status => (RecoveredAt is null ? "活动" : "已恢复") + (AcknowledgedAt is null ? " / 未确认" : " / 已确认");
    public string RaisedText => DateTimeOffset.FromUnixTimeMilliseconds(RaisedAt).LocalDateTime.ToString("MM-dd HH:mm:ss");
    public string SeverityText => Severity switch { "Emergency" => "紧急", "Warning" => "警告", _ => "提示" };
}
public sealed class HistoryRow
{
    public long Id { get; set; }
    public string TagId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public long Timestamp { get; set; }
    public double Value { get; set; }
    public string Quality { get; set; } = "Good";
    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
}
public sealed class UserRow
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "Operator";
    public bool Enabled { get; set; } = true;
}
public sealed class CommunicationSettings
{
    public string Mode { get; set; } = "TCP";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1502;
    public string SerialPort { get; set; } = "COM5";
    public int BaudRate { get; set; } = 9600;
    public string Parity { get; set; } = "Even";
    public int DataBits { get; set; } = 8;
    public string StopBits { get; set; } = "One";
    public int PollIntervalMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 1000;
    public int Retries { get; set; } = 2;
    public int RetentionDays { get; set; } = 30;
}
public sealed record LiveValue(TagRow Tag, double Value, DataQuality Quality, DateTimeOffset Timestamp);
public sealed record StationSnapshot(IReadOnlyList<LiveValue> Values, ConnectionState Connection, double PollMilliseconds, long PollCount, DateTimeOffset UpdatedAt, string? Error);
public interface IPollingSource
{
    StationSnapshot Snapshot { get; }
}
