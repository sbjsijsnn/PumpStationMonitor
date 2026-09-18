using PumpStationMonitor.Core.Models;
using PumpStationMonitor.Core.Protocol;

namespace PumpStationMonitor.Core.Services;

// 承载设备配置，连接存储、运行和展示层。
// 设备行独立于点位，站号只在一处维护。
// 只描述数据契约，不在模型中执行设备访问或数据库操作。
public sealed class DeviceRow
{
    public string Id
    {
        get;
        set;
    } = "";
    public string Name
    {
        get;
        set;
    } = "";
    public int UnitId
    {
        get;
        set;
    } = 1;
}

// 承载点位配置，连接存储、运行和展示层。
// 字段与持久化点表对应，编辑和运行时可共享契约。
// 只描述数据契约，不在模型中执行设备访问或数据库操作。
public sealed class TagRow
{
    public string Id
    {
        get;
        set;
    } = "";
    public string DeviceId
    {
        get;
        set;
    } = "pump1";
    public string Name
    {
        get;
        set;
    } = "新点位";
    public int Function
    {
        get;
        set;
    } = 3;
    public int Address
    {
        get;
        set;
    }
    public string Unit
    {
        get;
        set;
    } = "";
    public double Scale
    {
        get;
        set;
    } = 1;
    public double Offset
    {
        get;
        set;
    }
    public int? Bit
    {
        get;
        set;
    }
    public string DataType
    {
        get;
        set;
    } = "UInt16";
    public string ByteOrder
    {
        get;
        set;
    } = "Abcd";
    public bool Enabled
    {
        get;
        set;
    } = true;
}

// 承载报警规则配置，连接存储、运行和展示层。
// 可编辑配置与报警引擎的不可变规则分开。
// 只描述数据契约，不在模型中执行设备访问或数据库操作。
public sealed class RuleRow
{
    public string Id
    {
        get;
        set;
    } = "";
    public string TagId
    {
        get;
        set;
    } = "level";
    public string Kind
    {
        get;
        set;
    } = "H";
    public double LimitValue
    {
        get;
        set;
    } = 8;
    public double Deadband
    {
        get;
        set;
    } = 0.3;
    public int DelayMs
    {
        get;
        set;
    } = 1000;
    public string Severity
    {
        get;
        set;
    } = "Warning";
    public bool Enabled
    {
        get;
        set;
    } = true;
}

// 承载报警事件记录，连接存储、运行和展示层。
// 发生、恢复和确认时间独立保存，便于展示完整生命周期。
// 只描述数据契约，不在模型中执行设备访问或数据库操作。
public sealed class AlarmRow
{
    public string Id
    {
        get;
        set;
    } = "";
    public string RuleId
    {
        get;
        set;
    } = "";
    public string TagId
    {
        get;
        set;
    } = "";
    public string Name
    {
        get;
        set;
    } = "";
    public string Severity
    {
        get;
        set;
    } = "";
    public long RaisedAt
    {
        get;
        set;
    }
    public long? RecoveredAt
    {
        get;
        set;
    }
    public long? AcknowledgedAt
    {
        get;
        set;
    }
    public string? AcknowledgedBy
    {
        get;
        set;
    }
    public double Value
    {
        get;
        set;
    }
    public string Status => (RecoveredAt is null ? "活动" : "已恢复") + (AcknowledgedAt is null ? " / 未确认" : " / 已确认");
    public string RaisedText => DateTimeOffset.FromUnixTimeMilliseconds(RaisedAt).LocalDateTime.ToString(
        "MM-dd HH:mm:ss");
    public string SeverityText => Severity switch
    {
        "Emergency" => "紧急",
        "Warning" => "警告",
        _ => "提示"
    };
}

// 承载历史采样记录，连接存储、运行和展示层。
// 把设备、点位、时间和质量保存在同一条记录中。
// 只描述数据契约，不在模型中执行设备访问或数据库操作。
public sealed class HistoryRow
{
    public long Id
    {
        get;
        set;
    }
    public string TagId
    {
        get;
        set;
    } = "";
    public string DeviceId
    {
        get;
        set;
    } = "";
    public long Timestamp
    {
        get;
        set;
    }
    public double Value
    {
        get;
        set;
    }
    public string Quality
    {
        get;
        set;
    } = "Good";
    public string TimeText => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).LocalDateTime.ToString(
        "yyyy-MM-dd HH:mm:ss.fff");
}

// 承载用户持久化记录，连接存储、运行和展示层。
// 账户与角色集中传递，界面读取时剔除密码散列。
// 只描述数据契约，不在模型中执行设备访问或数据库操作。
public sealed class UserRow
{
    public string Username
    {
        get;
        set;
    } = "";
    public string PasswordHash
    {
        get;
        set;
    } = "";
    public string Role
    {
        get;
        set;
    } = "Operator";
    public bool Enabled
    {
        get;
        set;
    } = true;
}

// 承载通信与保存策略配置，连接存储、运行和展示层。
// 用属性默认值兼容已有数据库中的配置文档。
// 只描述数据契约，不在模型中执行设备访问或数据库操作。
public sealed class CommunicationSettings
{
    public string Mode
    {
        get;
        set;
    } = "TCP";
    public string Host
    {
        get;
        set;
    } = "127.0.0.1";
    public int Port
    {
        get;
        set;
    } = 1502;
    public string SerialPort
    {
        get;
        set;
    } = "COM5";
    public int BaudRate
    {
        get;
        set;
    } = 9600;
    public string Parity
    {
        get;
        set;
    } = "Even";
    public int DataBits
    {
        get;
        set;
    } = 8;
    public string StopBits
    {
        get;
        set;
    } = "One";
    public int PollIntervalMs
    {
        get;
        set;
    } = 1000;
    public int TimeoutMs
    {
        get;
        set;
    } = 1000;
    public int Retries
    {
        get;
        set;
    } = 2;
    public int RetentionDays
    {
        get;
        set;
    } = 30;
    public Dictionary<int, PumpControlSettings> PumpControls
    {
        get;
        set;
    } = new()
    {
        [1] = new()
        {
            DeviceId = "pump1",
            CoilAddress = 0,
            RegisterAddress = 40
        },
        [2] = new()
        {
            DeviceId = "pump2",
            CoilAddress = 1,
            RegisterAddress = 41
        }
    };
}

// 承载一次实时采样，连接存储、运行和展示层。
// 点位定义、数值、质量和时间一起传递，避免字段错配。
// 只描述数据契约，不在模型中执行设备访问或数据库操作。
public sealed record LiveValue(
    TagRow Tag,
    double Value,
    DataQuality Quality,
    DateTimeOffset Timestamp
);
// 承载完整一轮采集结果，连接存储、运行和展示层。
// 一次替换整份快照，让界面读取保持一致。
// 只描述数据契约，不在模型中执行设备访问或数据库操作。
public sealed record StationSnapshot(
    IReadOnlyList<LiveValue> Values,
    ConnectionState Connection,
    double PollMilliseconds,
    long PollCount,
    DateTimeOffset UpdatedAt,
    string? Error
);
public interface IPollingSource
{
    StationSnapshot Snapshot
    {
        get;
    }
}

// 承载泵号到设备及控制地址的映射，连接存储、运行和展示层。
// 把地址和启停值从控制流程移到配置，保持默认泵站行为。
// 只描述数据契约，不在模型中执行设备访问或数据库操作。
public sealed class PumpControlSettings
{
    public string DeviceId
    {
        get;
        set;
    } = "";
    public ushort CoilAddress
    {
        get;
        set;
    }
    public ushort RegisterAddress
    {
        get;
        set;
    }
    public ushort StartValue
    {
        get;
        set;
    } = 1;
    public ushort StopValue
    {
        get;
        set;
    }
}
