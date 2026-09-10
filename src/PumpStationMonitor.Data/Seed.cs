using PumpStationMonitor.Core.Services;
namespace PumpStationMonitor.Data;

public static class Seed
{
    public static DeviceRow[] Devices => [new() { Id = "pump1", Name = "1# 水泵" }, new() { Id = "pump2", Name = "2# 水泵" }, new() { Id = "sump", Name = "集水池" }, new() { Id = "outlet", Name = "出水管路" }];
    public static List<TagRow> Tags
    {
        get
        {
            var tags = new List<TagRow>();
            for (int pump = 1; pump <= 2; pump++)
            {
                string device = $"pump{pump}"; int address = (pump - 1) * 10;
                string[] names = ["运行", "故障", "远程", "自动"];
                for (int bit = 0; bit < 4; bit++) tags.Add(new() { Id = $"{device}_{new[] { "run", "fault", "remote", "auto" }[bit]}", DeviceId = device, Name = $"{pump}# 泵{names[bit]}", Address = address, Bit = bit });
                tags.Add(new() { Id = $"{device}_current", DeviceId = device, Name = $"{pump}# 泵电流", Address = address + 1, Scale = .1, Unit = "A" });
                tags.Add(new() { Id = $"{device}_frequency", DeviceId = device, Name = $"{pump}# 泵频率", Address = address + 2, Scale = .1, Unit = "Hz" });
                tags.Add(new() { Id = $"{device}_hours", DeviceId = device, Name = $"{pump}# 泵累计运行", Address = address + 3, Unit = "h" });
                tags.Add(new() { Id = $"{device}_command", DeviceId = device, Name = $"{pump}# 泵启停", Function = 1, Address = pump - 1 });
            }
            tags.AddRange([
                new(){Id="level",DeviceId="sump",Name="集水池液位",Address=20,Scale=.01,Unit="m"},
                new(){Id="inflow",DeviceId="sump",Name="进水流量",Address=21,Scale=.1,Unit="m³/h"},
                new(){Id="high_switch",DeviceId="sump",Name="高液位开关",Address=22,Bit=0},
                new(){Id="low_switch",DeviceId="sump",Name="低液位开关",Address=22,Bit=1},
                new(){Id="pressure",DeviceId="outlet",Name="出口压力",Address=30,Scale=.001,Unit="MPa"},
                new(){Id="outflow",DeviceId="outlet",Name="出口流量",Address=31,Scale=.1,Unit="m³/h"},
                new(){Id="valve",DeviceId="outlet",Name="阀门开度",Address=32,Scale=.1,Unit="%"}]);
            return tags;
        }
    }
    public static RuleRow[] Rules => [
        new(){Id="pump1_fault",TagId="pump1_fault",Kind="Bit",LimitValue=1,Deadband=0,DelayMs=0,Severity="Emergency"},
        new(){Id="pump2_fault",TagId="pump2_fault",Kind="Bit",LimitValue=1,Deadband=0,DelayMs=0,Severity="Emergency"},
        new(){Id="level_hh",TagId="level",Kind="HH",LimitValue=9,Severity="Emergency"},
        new(){Id="level_h",TagId="level",Kind="H",LimitValue=8},
        new(){Id="level_l",TagId="level",Kind="L",LimitValue=2},
        new(){Id="level_ll",TagId="level",Kind="LL",LimitValue=1,Severity="Emergency"}];
}
