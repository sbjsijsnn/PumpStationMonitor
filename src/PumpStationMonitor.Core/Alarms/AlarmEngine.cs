using PumpStationMonitor.Core.Models;
namespace PumpStationMonitor.Core.Alarms;
public enum AlarmKind { Bit, HH, H, L, LL }
public enum AlarmSeverity { Information, Warning, Emergency }
public enum AlarmTransition { Raised, Recovered, Acknowledged }
public sealed record AlarmRule(string Id, string TagId, AlarmKind Kind, double Limit, double Deadband, TimeSpan Delay, AlarmSeverity Severity);
public sealed record AlarmEvent(Guid OccurrenceId, string RuleId, AlarmTransition Transition, DateTimeOffset Timestamp, double Value, AlarmSeverity Severity, string? User = null);

// 串行处理采样；使用采样时间以支持确定性测试和历史回放。
public sealed class AlarmEngine
{
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
        if (this.rules.Values.Any(r => r.Deadband < 0 || !double.IsFinite(r.Deadband) || !double.IsFinite(r.Limit) || r.Delay < TimeSpan.Zero))
            throw new ArgumentException("Invalid alarm configuration", nameof(rules));
        states = this.rules.Keys.ToDictionary(id => id, _ => new State());
    }
    public IReadOnlyList<AlarmEvent> Evaluate(TagSample sample)
    {
        var result = new List<AlarmEvent>();
        foreach (var rule in rules.Values.Where(r => r.TagId == sample.TagId))
        {
            var state = states[rule.Id];
            if (state.Last is { } last && sample.Timestamp < last) throw new ArgumentException("Samples must be chronological");
            state.Last = sample.Timestamp;
            if (sample.Quality != DataQuality.Good || !double.IsFinite(sample.Value)) { state.Pending = null; continue; }
            state.Value = sample.Value;
            bool high = rule.Kind is AlarmKind.H or AlarmKind.HH;
            bool triggered = rule.Kind == AlarmKind.Bit ? sample.Value != 0 : high ? sample.Value >= rule.Limit : sample.Value <= rule.Limit;
            bool recovered = rule.Kind == AlarmKind.Bit ? sample.Value == 0 : high ? sample.Value < rule.Limit - rule.Deadband : sample.Value > rule.Limit + rule.Deadband;
            if (state.Active)
            {
                if (recovered)
                {
                    state.Active = false;
                    state.Pending = null;
                    result.Add(new(state.Occurrence, rule.Id, AlarmTransition.Recovered, sample.Timestamp, sample.Value, rule.Severity));
                }
            }
            else if (triggered)
            {
                state.Pending ??= sample.Timestamp;
                if (sample.Timestamp - state.Pending.Value >= rule.Delay)
                {
                    state.Active = true;
                    state.Acknowledged = false;
                    state.Occurrence = Guid.NewGuid();
                    result.Add(new(state.Occurrence, rule.Id, AlarmTransition.Raised, sample.Timestamp, sample.Value, rule.Severity));
                }
            }
            else state.Pending = null;
        }
        return result;
    }
    public AlarmEvent? Acknowledge(string ruleId, string user, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        var state = states[ruleId];
        if (!state.Active || state.Acknowledged) return null;
        if (state.Last is { } last && timestamp < last) throw new ArgumentException("Acknowledgement predates sample");
        state.Acknowledged = true;
        return new(state.Occurrence, ruleId, AlarmTransition.Acknowledged, timestamp, state.Value, rules[ruleId].Severity, user);
    }
}
