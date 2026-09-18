using PumpStationMonitor.Core.Models;

namespace PumpStationMonitor.Core.Alarms;

public enum AlarmKind
{
    Bit,
    HH,
    H,
    L,
    LL
}

public enum AlarmSeverity
{
    Information,
    Warning,
    Emergency
}

public enum AlarmTransition
{
    Raised,
    Recovered,
    Acknowledged
}

// 描述一个点位的报警阈值、死区、延时和级别。
// 采用不可变记录，评估过程中规则不会被意外修改。
// 规则编号用于关联持久化事件和运行状态。
public sealed record AlarmRule(
    string Id,
    string TagId,
    AlarmKind Kind,
    double Limit,
    double Deadband,
    TimeSpan Delay,
    AlarmSeverity Severity
);
// 描述一次报警状态变化及其发生编号。
// 采用不可变事件，在运行时与数据库之间传递明确的变化事实。
// 发生和恢复共用编号，便于重试持久化而不重复创建记录。
public sealed record AlarmEvent(
    Guid OccurrenceId,
    string RuleId,
    AlarmTransition Transition,
    DateTimeOffset Timestamp,
    double Value,
    AlarmSeverity Severity,
    string? User = null
);
// 串行处理采样；使用采样时间以支持确定性测试和历史回放。
// 把有序采样转换成报警发生、恢复和确认事件。
// 每条规则独立保存延时与活动状态，避免不同点位相互影响。
// 使用采样时间而非系统时钟，使边界行为可以确定性验证。
public sealed class AlarmEngine
{
    // 保存单条报警规则的等待时间、活动编号和确认状态。
    // 只在报警引擎内部串行更新，避免状态向调用方泄漏。
    // 将规则配置与运行状态分开，便于恢复已有报警。
    private sealed class State
    {
        public DateTimeOffset? Pending;
        public DateTimeOffset? Last;
        public Guid Occurrence;
        public bool Active;
        public bool Acknowledged;
        public double Value;
    }

    private readonly Dictionary<string, AlarmRule> rules;
    private readonly Dictionary<string, State> states;
    public AlarmEngine(IEnumerable<AlarmRule> rules)
    {
        this.rules = rules.ToDictionary(r => r.Id);
        if (this.rules.Values.Any(
                r => r.Deadband < 0 || !double.IsFinite(r.Deadband) || !double.IsFinite(r.Limit)
                    || r.Delay < TimeSpan.Zero
        ))
        {
            throw new ArgumentException("Invalid alarm configuration", nameof(rules));
        }

        states = this.rules.Keys.ToDictionary(id => id, _ => new State());
    }

    public IReadOnlyList<AlarmEvent> Evaluate(TagSample sample)
    {
        // 仅评估当前点位的规则；所有状态均由采样时间推进。
        var result = new List<AlarmEvent>();
        foreach (var rule in rules.Values.Where(r => r.TagId == sample.TagId))
        {
            // 同一规则要求时间递增，避免旧样本改变已经确认的状态。
            var state = states[rule.Id];
            if (state.Last is { } last && sample.Timestamp < last)
            {
                throw new ArgumentException("Samples must be chronological");
            }

            state.Last = sample.Timestamp;
            // 坏质量打断越限计时，但不能证明设备已恢复。
            if (sample.Quality != DataQuality.Good || !double.IsFinite(sample.Value))
            {
                state.Pending = null;
                continue;
            }

            state.Value = sample.Value;
            // 按高低限计算触发与恢复条件，死区只作用于恢复边界。
            bool high = rule.Kind is AlarmKind.H or AlarmKind.HH;
            bool triggered = rule.Kind == AlarmKind.Bit
                ? sample.Value != 0
                : high ? sample.Value >= rule.Limit : sample.Value <= rule.Limit;
            bool recovered = rule.Kind == AlarmKind.Bit
                ? sample.Value == 0
                : high
                ? sample.Value < rule.Limit - rule.Deadband
                : sample.Value > rule.Limit + rule.Deadband;
            // 活动报警只检查恢复，不重复创建发生记录。
            if (state.Active)
            {
                if (recovered)
                {
                    state.Active = false;
                    state.Pending = null;
                    result.Add(
                        new(
                            state.Occurrence,
                            rule.Id,
                            AlarmTransition.Recovered,
                            sample.Timestamp,
                            sample.Value,
                            rule.Severity
                        )
                    );
                }
            }
            else if (triggered)
            {
                // 首次越限开始计时；持续达到延时才创建新的报警发生编号。
                state.Pending ??= sample.Timestamp;
                if (sample.Timestamp - state.Pending.Value >= rule.Delay)
                {
                    state.Active = true;
                    state.Acknowledged = false;
                    state.Occurrence = Guid.NewGuid();
                    result.Add(
                        new(
                            state.Occurrence,
                            rule.Id,
                            AlarmTransition.Raised,
                            sample.Timestamp,
                            sample.Value,
                            rule.Severity
                        )
                    );
                }
            }
            // 正常采样清空等待时间，下次越限重新计时。
            else
            {
                state.Pending = null;
            }
        }

        return result;
    }

    public void RestoreActive(
        string ruleId,
        Guid occurrence,
        double value,
        DateTimeOffset raisedAt,
        bool acknowledged
    )
    {
        var state = states[ruleId];
        state.Active = true;
        state.Occurrence = occurrence;
        state.Value = value;
        state.Last = raisedAt;
        state.Acknowledged = acknowledged;
    }

    public AlarmEvent? Acknowledge(string ruleId, string user, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        var state = states[ruleId];
        if (!state.Active || state.Acknowledged)
        {
            return null;
        }

        if (state.Last is { } last && timestamp < last)
        {
            throw new ArgumentException("Acknowledgement predates sample");
        }

        state.Acknowledged = true;
        return new(
            state.Occurrence,
            ruleId,
            AlarmTransition.Acknowledged,
            timestamp,
            state.Value,
            rules[ruleId].Severity,
            user
        );
    }
}
