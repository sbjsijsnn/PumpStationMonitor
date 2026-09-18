using PumpStationMonitor.Core.Alarms;
using PumpStationMonitor.Core.Models;

namespace PumpStationMonitor.Tests;

// 验证报警状态机的既有行为和边界条件。
// 使用可重复的输入与断言，确保重构后外部行为保持一致。
// 测试数据与运行状态隔离，便于单独执行和定位回归。
public class AlarmTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-10T00:00:00Z");
    private static TagSample Sample(
        double value,
        int second,
        DataQuality quality = DataQuality.Good
    ) => new("level", value, quality, Start.AddSeconds(second));
    private static AlarmEngine Engine(
        AlarmKind kind = AlarmKind.H,
        double limit = 8,
        double deadband = 1,
        int delay = 2
    ) => new(
        [
            new(
                "r",
                "level",
                kind,
                limit,
                deadband,
                TimeSpan.FromSeconds(delay),
                AlarmSeverity.Warning
            )
        ]
    );
    [Fact]
    public void DelayDeadbandAndAcknowledgement()
    {
        var engine = Engine();
        Assert.Empty(engine.Evaluate(Sample(8, 0)));
        Assert.Empty(engine.Evaluate(Sample(9, 1)));
        var raised = Assert.Single(engine.Evaluate(Sample(9, 2)));
        Assert.Equal(AlarmTransition.Raised, raised.Transition);
        Assert.Empty(engine.Evaluate(Sample(7.5, 3)));
        var ack = engine.Acknowledge("r", "admin", Start.AddSeconds(3));
        Assert.NotNull(ack);
        Assert.Equal(raised.OccurrenceId, ack.OccurrenceId);
        Assert.Null(engine.Acknowledge("r", "admin", Start.AddSeconds(3)));
        Assert.Empty(engine.Evaluate(Sample(7, 4)));
        var recovered = Assert.Single(engine.Evaluate(Sample(6.9, 5)));
        Assert.Equal(AlarmTransition.Recovered, recovered.Transition);
        Assert.Equal(raised.OccurrenceId, recovered.OccurrenceId);
        Assert.Empty(engine.Evaluate(Sample(9, 6)));
        Assert.NotEqual(
            raised.OccurrenceId,
            Assert.Single(engine.Evaluate(Sample(9, 8))).OccurrenceId
        );
    }

    [Fact]
    public void BriefExcursionResetsDelay()
    {
        var engine = Engine();
        engine.Evaluate(Sample(9, 0));
        engine.Evaluate(Sample(6, 1));
        Assert.Empty(engine.Evaluate(Sample(9, 2)));
        Assert.Empty(engine.Evaluate(Sample(9, 3)));
        Assert.Single(engine.Evaluate(Sample(9, 4)));
    }

    [Theory]
    [InlineData(AlarmKind.H)]
    [InlineData(AlarmKind.HH)]
    [InlineData(AlarmKind.L)]
    [InlineData(AlarmKind.LL)]
    public void FourLimitDirections(AlarmKind kind)
    {
        bool high = kind is AlarmKind.H or AlarmKind.HH;
        var engine = Engine(kind, 8, 1, 0);
        Assert.Single(engine.Evaluate(Sample(8, 0)));
        Assert.Empty(engine.Evaluate(Sample(high ? 7 : 9, 1)));
        Assert.Equal(
            AlarmTransition.Recovered,
            Assert.Single(engine.Evaluate(Sample(high ? 6 : 10, 2))).Transition
        );
    }

    [Fact]
    public void BitAlarmAndBadQuality()
    {
        var engine = Engine(AlarmKind.Bit, 0, 0, 0);
        Assert.Single(engine.Evaluate(Sample(1, 0)));
        Assert.Empty(engine.Evaluate(Sample(0, 1, DataQuality.Bad)));
        Assert.Single(engine.Evaluate(Sample(0, 2)));
    }

    [Fact]
    public void BadQualityBreaksPendingAndOutOfOrderFails()
    {
        var engine = Engine();
        engine.Evaluate(Sample(9, 0));
        engine.Evaluate(Sample(double.NaN, 2));
        Assert.Empty(engine.Evaluate(Sample(9, 3)));
        Assert.Single(engine.Evaluate(Sample(9, 5)));
        Assert.Throws<ArgumentException>(() => engine.Evaluate(Sample(9, 4)));
    }
}
