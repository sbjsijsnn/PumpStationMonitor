namespace PumpStationMonitor.Core.Diagnostics;

// 计时器暂停或机器休眠不能算作连续运行；出现长间隔后重新计算连续段。
// 根据相邻观测的时间差计算连续运行时长。
// 把连续性判断从性能采样脚本抽出，便于验证暂停与恢复场景。
// 超过允许间隔即重新计时，避免把休眠算成连续运行。
public sealed class ContinuityTracker(double maximumGapSeconds = 5)
{
    private double previous;
    public double ContinuousSeconds
    {
        get;
        private set;
    }
    public double LongestContinuousSeconds
    {
        get;
        private set;
    }
    public int Interruptions
    {
        get;
        private set;
    }
    public double MaximumGapSeconds
    {
        get;
        private set;
    }

    public void Observe(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < previous)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        }

        double gap = elapsedSeconds - previous;
        previous = elapsedSeconds;
        MaximumGapSeconds = Math.Max(MaximumGapSeconds, gap);
        if (gap > maximumGapSeconds)
        {
            Interruptions++;
            ContinuousSeconds = 0;
        }
        else
        {
            ContinuousSeconds += gap;
        }

        LongestContinuousSeconds = Math.Max(LongestContinuousSeconds, ContinuousSeconds);
    }
}
