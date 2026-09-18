using PumpStationMonitor.Core.Diagnostics;
using PumpStationMonitor.Simulator;

Console.InputEncoding = System.Text.Encoding.UTF8;
Console.OutputEncoding = System.Text.Encoding.UTF8;
try
{
    int port = args.Length > 0 && int.TryParse(args[0], out var parsed)
        ? parsed
        : 1502;
    var station = new StationState();
    await using var server = new ModbusServer(station, new ConsoleLogger<ModbusServer>(args.Contains("--debug")));
    server.Start(port);
    Console.WriteLine($"泵站模拟器 127.0.0.1:{server.Port}，站号 1");
    Console.WriteLine("fault 1 | fault 2 | level 9.5 | offline | recover | quit");
    using var stopping = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopping.Cancel();
    };
    while (!stopping.IsCancellationRequested)
    {
        var line = await Console.In.ReadLineAsync(stopping.Token);
        if (line is null)
        {
            await Task.Delay(Timeout.Infinite, stopping.Token);
            break;
        }

        if (line.Trim() == "quit")
        {
            break;
        }

        try
        {
            station.Command(line.Trim());
            Console.WriteLine("已执行：" + line);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
        }
    }

    return 0;
}
catch (OperationCanceledException ex)
{
    Console.WriteLine("模拟器已停止：" + ex.Message);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
