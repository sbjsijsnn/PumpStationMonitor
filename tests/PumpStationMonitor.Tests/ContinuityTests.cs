using PumpStationMonitor.Core.Diagnostics;

namespace PumpStationMonitor.Tests;

// 验证连续运行计时的既有行为和边界条件。
// 使用可重复的输入与断言，确保重构后外部行为保持一致。
// 测试数据与运行状态隔离，便于单独执行和定位回归。
public class ContinuityTests
{
    [Fact]
    public void SleepDoesNotCountAsContinuousRuntime()
    {
        var tracker = new ContinuityTracker();
        for (int i = 1; i <= 100; i++)
        {
            tracker.Observe(i);
        }

        tracker.Observe(36100);
        Assert.Equal(0, tracker.ContinuousSeconds);
        Assert.Equal(1, tracker.Interruptions);
        Assert.Equal(100, tracker.LongestContinuousSeconds);
        tracker.Observe(36101);
        Assert.Equal(1, tracker.ContinuousSeconds);
    }

    [Fact]
    public void SmallSchedulingJitterCountsAndClockCannotGoBackwards()
    {
        var tracker = new ContinuityTracker();
        tracker.Observe(1.1);
        tracker.Observe(2.2);
        Assert.Equal(2.2, tracker.ContinuousSeconds, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Observe(1));
    }
}
