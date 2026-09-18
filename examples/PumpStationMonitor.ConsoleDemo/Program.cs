using PumpStationMonitor.Core.Diagnostics;
using PumpStationMonitor.Core.Protocol;

Console.InputEncoding = System.Text.Encoding.UTF8;
Console.OutputEncoding = System.Text.Encoding.UTF8;
try
{
    bool rtu = args.Length > 0 && args[0] == "--rtu";
    bool debug = args.Contains("--debug");
    await using IModbusClient client = rtu
        ? new ModbusRtuClient(
        new SerialSettings(args[1]),
        new ModbusOptions(),
        new ConsoleLogger<ModbusRtuClient>(debug)
    )
        : new ModbusTcpClient(
        "127.0.0.1",
        args.Length > 0 && int.TryParse(args[0], out int port) ? port : 1502,
        new ModbusOptions(),
        new ConsoleLogger<ModbusTcpClient>(debug)
    );
    client.StateChanged += state => Console.WriteLine($"连接状态：{state}");
    await client.WriteRegisterAsync(1, 100, 1234);
    if ((await client.ReadRegistersAsync(1, 3, 100, 1))[0] != 1234)
    {
        throw new InvalidDataException("Single register readback failed");
    }

    await client.WriteRegistersAsync(1, 100, [111, 222, 333]);
    if (!(await client.ReadRegistersAsync(1, 3, 100, 3)).SequenceEqual(new ushort[] { 111, 222, 333 }))
    {
        throw new InvalidDataException("Multiple register readback failed");
    }

    await client.WriteCoilAsync(1, 0, true);
    if (!(await client.ReadBitsAsync(1, 1, 0, 1))[0])
    {
        throw new InvalidDataException("Coil readback failed");
    }

    Console.WriteLine("读写回读通过：单寄存器 1234，多寄存器 111/222/333，1# 泵启动线圈为 true。");
    for (int i = 0; i < 5; i++)
    {
        var level = await client.ReadRegistersAsync(1, 4, 20, 1);
        var faults = await client.ReadBitsAsync(1, 2, 0, 4);
        Console.WriteLine($"液位 {level[0] * 0.01:F2} m；状态输入 {string.Join(",", faults)}");
        await Task.Delay(1000);
    }

    await client.WriteCoilAsync(1, 0, false);
    if (!rtu)
    {
        Console.WriteLine("RTU 物理链路验证：跳过（未指定虚拟串口）；RTU 报文由自动测试验证。");
    }

    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
