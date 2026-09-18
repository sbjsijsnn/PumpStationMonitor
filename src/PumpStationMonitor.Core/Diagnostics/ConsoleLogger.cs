using Microsoft.Extensions.Logging;

namespace PumpStationMonitor.Core.Diagnostics;

// M1 控制台适配器；后续宿主接入 Serilog，协议层仍依赖 ILogger。
// 为示例程序提供简单的分级控制台日志。
// 直接实现日志接口，使协议代码仍可依赖标准日志抽象。
// 调试输出可选开启，便于观察通信而不改动客户端。
public sealed class ConsoleLogger<T>(bool debug = false) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
    where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= (debug ? LogLevel.Debug : LogLevel.Information);
    public void Log<TState>(
        LogLevel level,
        EventId id,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        if (IsEnabled(level))
        {
            Console.Error.WriteLine(
                $"{DateTimeOffset.Now:O} [{level}] {typeof(T).Name}: {formatter(state, exception)} {exception}"
            );
        }
    }
}
