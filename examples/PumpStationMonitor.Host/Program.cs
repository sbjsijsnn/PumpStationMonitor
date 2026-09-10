using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PumpStationMonitor.Core.Services;
using PumpStationMonitor.Data;
using PumpStationMonitor.Data.Services;
using PumpStationMonitor.Simulator;
Console.OutputEncoding = System.Text.Encoding.UTF8;
string Option(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
try
{
    string output = Path.GetFullPath(Option("--output", Path.Combine("artifacts", "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")))); Directory.CreateDirectory(output);
    Environment.SetEnvironmentVariable("PUMP_DATA_DIR", output);
    int seconds = int.Parse(Option("--seconds", "600"), CultureInfo.InvariantCulture);
    var builder = Bootstrap.Create([], Path.Combine(output, "station.db")); using var host = builder.Build(); await Bootstrap.InitializeAsync(host, builder.Configuration);
    var repository = host.Services.GetRequiredService<StationRepository>(); var settings = await repository.SettingsAsync();
    var station = new StationState(); await using var server = new ModbusServer(station, host.Services.GetRequiredService<ILogger<ModbusServer>>()); server.Start(0);
    settings.Mode = "TCP"; settings.Host = "127.0.0.1"; settings.Port = server.Port; await repository.SaveSettingsAsync(settings);
    if (args.Contains("--benchmark"))
    {
        var measurements = new List<object>(); long start = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        for (int target = 100000; target <= 500000; target += 400000)
        {
            long count = await repository.HistoryCountAsync(); var insert = Stopwatch.StartNew();
            for (long i = count; i < target; i += 2000)
                await repository.InsertHistoryAsync(Enumerable.Range(0, (int)Math.Min(2000, target - i)).Select(j => new HistoryRow { TagId = "benchmark", DeviceId = "sump", Timestamp = start + i + j, Value = (i + j) % 1000 * .01 }).ToArray());
            var times = new List<double>(); int rows = 0;
            for (int run = 0; run < 6; run++) { var watch = Stopwatch.StartNew(); var found = await repository.QueryHistoryAsync("sump", "benchmark", start, start + target, 10000); if (run > 0) times.Add(watch.Elapsed.TotalMilliseconds); rows = found.Count; }
            measurements.Add(new { totalRows = target, returnedRows = rows, queryLimit = 10000, warmRuns = 5, minMs = times.Min(), meanMs = times.Average(), maxMs = times.Max(), insertSeconds = insert.Elapsed.TotalSeconds });
        }
        await File.WriteAllTextAsync(Path.Combine(output, "benchmark.json"), JsonSerializer.Serialize(measurements, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine("Benchmark complete: " + output); return 0;
    }
    await host.StartAsync(); var runtime = host.Services.GetRequiredService<StationRuntime>(); var writer = host.Services.GetRequiredService<HistoryWriter>();
    using var stop = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    var elapsed = Stopwatch.StartNew(); var process = Process.GetCurrentProcess(); var previousCpu = process.TotalProcessorTime; double previousSeconds = 0;
    var pollTimes = new List<double>(); long lastPoll = 0; long peakMemory = 0; int badSamples = 0;
    await using (var csv = new StreamWriter(Path.Combine(output, "runtime.csv"), false, System.Text.Encoding.UTF8))
    {
        await csv.WriteLineAsync("elapsed_seconds,working_set_mb,private_mb,cpu_percent,poll_count,poll_ms,history_written,queue_pending,quality");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (elapsed.Elapsed.TotalSeconds < seconds && await timer.WaitForNextTickAsync(stop.Token))
            {
                process.Refresh(); double now = elapsed.Elapsed.TotalSeconds; var cpu = process.TotalProcessorTime;
                double percent = (cpu - previousCpu).TotalSeconds / (now - previousSeconds) / Environment.ProcessorCount * 100; previousCpu = cpu; previousSeconds = now;
                var snapshot = runtime.Snapshot; peakMemory = Math.Max(peakMemory, process.WorkingSet64);
                if (snapshot.PollCount != lastPoll) { pollTimes.Add(snapshot.PollMilliseconds); lastPoll = snapshot.PollCount; }
                if (snapshot.Error is not null) badSamples++;
                string line = FormattableString.Invariant($"{now:F3},{process.WorkingSet64 / 1048576.0:F2},{process.PrivateMemorySize64 / 1048576.0:F2},{percent:F3},{snapshot.PollCount},{snapshot.PollMilliseconds:F3},{writer.Written},{writer.Pending},{(snapshot.Error is null ? "Good" : "Bad")}");
                await csv.WriteLineAsync(line); if ((int)now % 10 == 0) { await csv.FlushAsync(); Console.WriteLine(line); }
            }
        }
        catch (OperationCanceledException ex) when (stop.IsCancellationRequested) { Console.WriteLine("Cancelled: " + ex.Message); }
    }
    await host.StopAsync(); pollTimes.Sort();
    var result = new { completed = elapsed.Elapsed.TotalSeconds >= seconds, requestedSeconds = seconds, elapsedSeconds = elapsed.Elapsed.TotalSeconds, polls = runtime.Snapshot.PollCount, historyRows = await repository.HistoryCountAsync(), historyWritten = writer.Written, pending = writer.Pending, peakWorkingSetMb = peakMemory / 1048576.0, pollMeanMs = pollTimes.DefaultIfEmpty().Average(), pollP95Ms = pollTimes.Count > 0 ? pollTimes[(int)((pollTimes.Count - 1) * .95)] : 0, badSampleSeconds = badSamples, sdk = "10.0.400", processors = Environment.ProcessorCount };
    await File.WriteAllTextAsync(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine(JsonSerializer.Serialize(result)); return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
