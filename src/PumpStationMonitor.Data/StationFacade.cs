using System.Globalization;
using System.IO.Ports;
using Microsoft.Extensions.Logging;
using PumpStationMonitor.Core.Alarms;
using PumpStationMonitor.Core.Models;
using PumpStationMonitor.Core.Services;
using PumpStationMonitor.Data.Security;
using PumpStationMonitor.Data.Services;

namespace PumpStationMonitor.Data;

// 承接界面的保存、删除、控制和报警确认请求。
// 统一检查登录角色与业务约束，避免页面各自实现不一致的规则。
// 配置变更经运行时串行生效，账户变更单独串行保护管理员约束。
public sealed class StationFacade(
    StationRepository repository,
    StationRuntime runtime,
    SessionService session,
    ILogger<StationFacade> logger
)
{
    private readonly SemaphoreSlim mutations = new(1, 1);
    public async Task SaveSettingsAsync(CommunicationSettings settings)
    {
        await session.RequireAsync(true);
        bool invalidNetwork = settings.Mode is not ("TCP" or "RTU") || string.IsNullOrWhiteSpace(settings.Host)
            || settings.Port is < 1 or > 65535;
        bool invalidTiming = settings.PollIntervalMs is < 100 or > 60000 || settings.TimeoutMs is < 100 or > 30000
            || settings.Retries is < 0 or > 5;
        bool invalidSerial = settings.BaudRate is < 1200 or > 921600 || settings.DataBits is < 5 or > 8
            || string.IsNullOrWhiteSpace(settings.SerialPort);
        bool invalidParity = !Enum.TryParse<Parity>(settings.Parity, out var parity) || !Enum.IsDefined(parity);
        bool invalidStopBits = !Enum.TryParse<StopBits>(settings.StopBits, out var stop) || !Enum.IsDefined(stop)
            || stop == StopBits.None;
        bool invalidRetention = settings.RetentionDays is < 1 or > 3650;
        if (invalidNetwork || invalidTiming || invalidSerial || invalidParity || invalidStopBits
                || invalidRetention)
        {
            throw new ArgumentException("通信参数超出有效范围");
        }

        await runtime.ChangeConfigurationAsync(() => repository.SaveSettingsAsync(settings));
        logger.LogInformation(
            "User {User} changed communication settings to {Mode}",
            session.Username,
            settings.Mode
        );
    }

    public async Task SaveTagAsync(TagRow tag)
    {
        await session.RequireAsync(true);
        bool invalidIdentity = string.IsNullOrWhiteSpace(tag.Id) || string.IsNullOrWhiteSpace(tag.Name);
        bool invalidDevice = !invalidIdentity
            && !(await repository.DevicesAsync()).Any(device => device.Id == tag.DeviceId);
        bool invalidAddress = tag.Function is not (1 or 2 or 3 or 4) || tag.Address is < 0 or > 65535
            || tag.Bit is < 0 or > 15;
        bool invalidScaling = !double.IsFinite(tag.Scale) || !double.IsFinite(tag.Offset);
        bool invalidType = tag.DataType is not ("UInt16" or "Int16" or "Float32");
        bool invalidOrder = !Enum.TryParse<RegisterOrder>(tag.ByteOrder, out var order) || !Enum.IsDefined(order);
        bool invalidFloat = tag.DataType == "Float32" && (tag.Address == 65535 || tag.Bit.HasValue);
        bool invalidBit = tag.Function is 1 or 2 && (tag.Bit.HasValue || tag.DataType != "UInt16");
        if (invalidIdentity || invalidDevice || invalidAddress || invalidScaling || invalidType
                || invalidOrder
                || invalidFloat
                || invalidBit)
        {
            throw new ArgumentException("点位参数无效，请检查地址、类型与所属设备");
        }

        await runtime.ChangeConfigurationAsync(
            async () =>
            {
                if ((await repository.AlarmsAsync(activeOnly: true)).Any(a => a.TagId == tag.Id))
                {
                    throw new InvalidOperationException("点位仍有活动报警，恢复后才能修改");
                }

                await repository.SaveTagAsync(tag);
            }
        );
        logger.LogInformation("User {User} saved tag {Tag}", session.Username, tag.Id);
    }

    public async Task DeleteTagAsync(string id)
    {
        await session.RequireAsync(true);
        await runtime.ChangeConfigurationAsync(() => repository.DeleteTagAsync(id));
        logger.LogInformation("User {User} deleted tag {Id}", session.Username, id);
    }

    public async Task SaveRuleAsync(RuleRow rule)
    {
        await session.RequireAsync(true);
        bool missingId = string.IsNullOrWhiteSpace(rule.Id);
        bool missingTag = !missingId && !(await repository.TagsAsync()).Any(tag => tag.Id == rule.TagId);
        bool invalidKind = !Enum.TryParse<AlarmKind>(rule.Kind, out var kind) || !Enum.IsDefined(kind);
        bool invalidSeverity = !Enum.TryParse<AlarmSeverity>(rule.Severity, out var severity)
            || !Enum.IsDefined(severity);
        bool invalidLimits = !double.IsFinite(rule.LimitValue) || !double.IsFinite(rule.Deadband)
            || rule.Deadband < 0;
        bool invalidDelay = rule.DelayMs is < 0 or > 3600000;
        if (missingId || missingTag || invalidKind || invalidSeverity || invalidLimits
                || invalidDelay)
        {
            throw new ArgumentException("报警规则参数无效");
        }

        await runtime.ChangeConfigurationAsync(
            async () =>
            {
                if ((await repository.AlarmsAsync(activeOnly: true)).Any(a => a.RuleId == rule.Id))
                {
                    throw new InvalidOperationException("规则仍有活动报警，恢复后才能修改");
                }

                await repository.SaveRuleAsync(rule);
            }
        );
        logger.LogInformation("User {User} saved rule {Id}", session.Username, rule.Id);
    }

    public async Task DeleteRuleAsync(string id)
    {
        await session.RequireAsync(true);
        await runtime.ChangeConfigurationAsync(() => repository.DeleteRuleAsync(id));
        logger.LogInformation("User {User} deleted rule {Id}", session.Username, id);
    }

    public async Task SaveUserAsync(string name, string password, string role, bool enabled)
    {
        await session.RequireAsync(true);
        await mutations.WaitAsync();
        try
        {
            name = name.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length > 50
                    || role is not ("Admin" or "Operator"))
            {
                throw new ArgumentException("用户名或角色无效");
            }

            var old = await repository.UserAsync(name);
            bool removingAdmin = old is { Role: "Admin", Enabled: true } && (role != "Admin" || !enabled);
            bool lastAdmin = removingAdmin && await IsLastEnabledAdminAsync();
            if (lastAdmin)
            {
                throw new InvalidOperationException("至少保留一个启用的管理员");
            }

            string hash = string.IsNullOrEmpty(password) && old is not null
                ? old.PasswordHash
                : PasswordHasher.Hash(password);
            await repository.SaveUserAsync(
                new()
                {
                    Username = name.Trim(),
                    PasswordHash = hash,
                    Role = role,
                    Enabled = enabled
                }
            );
            logger.LogInformation("User {Actor} saved account {Name}", session.Username, name);
        }
        finally
        {
            mutations.Release();
        }
    }

    public async Task DeleteUserAsync(string name)
    {
        await session.RequireAsync(true);
        await mutations.WaitAsync();
        try
        {
            if (string.Equals(name, session.Username, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("不能删除当前登录用户");
            }

            var user = await repository.UserAsync(name);
            bool removingAdmin = user is { Role: "Admin", Enabled: true };
            bool lastAdmin = removingAdmin && await IsLastEnabledAdminAsync();
            if (lastAdmin)
            {
                throw new InvalidOperationException("至少保留一个启用的管理员");
            }

            await repository.DeleteUserAsync(name);
            logger.LogInformation("User {Actor} deleted account {Name}", session.Username, name);
        }
        finally
        {
            mutations.Release();
        }
    }

    public async Task ControlAsync(int pump, bool run, string mode)
    {
        await session.RequireAsync(true);
        if (mode is not ("Coil" or "Register"))
        {
            throw new ArgumentException("无效的控制模式");
        }

        logger.LogWarning(
            "Control requested by {User}: pump {Pump}, run {Run}, mode {Mode}",
            session.Username,
            pump,
            run,
            mode
        );
        try
        {
            await runtime.ControlAsync(pump, run, mode);
            logger.LogInformation("Control acknowledged for pump {Pump}", pump);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Control result unknown for pump {Pump}; read back before retry",
                pump
            );
            throw;
        }
    }

    public async Task AcknowledgeAsync(string id)
    {
        await session.RequireAsync();
        await repository.AcknowledgeAsync(id, session.Username!);
        logger.LogInformation("User {User} acknowledged alarm {Id}", session.Username, id);
    }

    public async Task ExportAlarmsAsync(string path, long from, long to)
    {
        await session.RequireAsync();
        var rows = await repository.AlarmsAsync(from, to);
        await using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
        await writer.WriteLineAsync("发生时间,点位,级别,状态,触发值,确认人,恢复时间,确认时间");
        foreach (var row in rows)
        {
            string raised = DateTimeOffset.FromUnixTimeMilliseconds(row.RaisedAt).ToString("O");
            string recovered = FormatOptionalTime(row.RecoveredAt);
            string acknowledged = FormatOptionalTime(row.AcknowledgedAt);
            string value = row.Value.ToString(CultureInfo.InvariantCulture);
            string[] cells = [
                raised,
                row.Name,
                row.SeverityText,
                row.Status,
                value,
                row.AcknowledgedBy ?? "",
                recovered,
                acknowledged
            ];
            string line = string.Join(',', cells.Select(CsvCell));
            await writer.WriteLineAsync(line);
        }

        logger.LogInformation(
            "User {User} exported {Count} alarms to {Path}",
            session.Username,
            rows.Count,
            path
        );
    }

    private async Task<bool> IsLastEnabledAdminAsync()
    {
        var users = await repository.UsersAsync();
        int administratorCount = users.Count(user => user.Role == "Admin" && user.Enabled);
        return administratorCount <= 1;
    }

    private static string FormatOptionalTime(long? timestamp)
    {
        if (!timestamp.HasValue)
        {
            return "";
        }

        var time = DateTimeOffset.FromUnixTimeMilliseconds(timestamp.Value);
        return time.ToString("O");
    }

    public static string CsvCell(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0]))
        {
            value = "'" + value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
