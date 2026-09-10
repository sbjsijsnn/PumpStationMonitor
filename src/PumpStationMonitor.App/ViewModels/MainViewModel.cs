using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PumpStationMonitor.Core.Models;
using PumpStationMonitor.Core.Services;
using PumpStationMonitor.Data;
using PumpStationMonitor.Data.Security;
using PumpStationMonitor.Data.Services;
using SkiaSharp;
namespace PumpStationMonitor.App.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly StationRepository repository;
    private readonly StationRuntime runtime;
    private readonly StationFacade facade;
    private readonly SessionService session;
    private readonly ILogger<MainViewModel> logger;
    private readonly DispatcherTimer timer;
    private bool refreshing;
    private long lastPoll = -1;
    private int refreshCounter;
    private readonly Dictionary<string, Queue<LiveValue>> trendBuffer = [];
    private readonly ObservableCollection<ObservablePoint> trendValues = [];
    public ObservableCollection<LiveRow> LiveRows { get; } = [];
    public ObservableCollection<DeviceCard> Devices { get; } = [new("pump1", "1# 水泵", "主用机组"), new("pump2", "2# 水泵", "备用机组"), new("sump", "集水池", "来水与液位"), new("outlet", "出水管路", "压力与输送")];
    public string[] Navigation { get; } = ["站点总览", "实时数据", "运行趋势", "报警中心", "历史查询", "系统设置"];
    public int[] Spans { get; } = [1, 5, 15, 60];
    public string[] Modes { get; } = ["TCP", "RTU"];
    public string[] Roles { get; } = ["Admin", "Operator"];
    public string[] Types { get; } = ["UInt16", "Int16", "Float32"];
    public string[] Orders { get; } = ["Abcd", "Badc", "Cdab", "Dcba"];
    public string[] Kinds { get; } = ["Bit", "HH", "H", "L", "LL"];
    public string[] Severities { get; } = ["Emergency", "Warning", "Information"];
    public string[] ControlModes { get; } = ["Coil", "Register"];
    public bool IsAdmin => session.IsAdmin;
    public IEnumerable<DeviceRow> StationDevices=>DeviceOptions.Where(d=>d.Id.Length>0);
    public bool IsEditingTag=>SelectedTag is not null;
    public bool IsEditingRule=>SelectedRule is not null;
    public string UserLabel => $"{session.Username} · {(IsAdmin ? "管理员" : "操作员")}";
    [ObservableProperty] private int page;
    [ObservableProperty] private string status = "正在读取泵站数据…";
    [ObservableProperty] private string connection = "连接中";
    [ObservableProperty] private string connectionColor = "#C38A24";
    [ObservableProperty] private string pollLabel = "等待采集";
    [ObservableProperty] private string clock = "";
    [ObservableProperty] private int activeCount;
    [ObservableProperty] private string alarmSummary = "无活动报警";
    [ObservableProperty] private List<TagRow> tags = [];
    [ObservableProperty,NotifyPropertyChangedFor(nameof(StationDevices))] private List<DeviceRow> deviceOptions = [];
    [ObservableProperty] private TagRow? trendTag;
    [ObservableProperty] private int spanMinutes = 5;
    [ObservableProperty] private List<AlarmRow> activeAlarms = [];
    [ObservableProperty] private List<AlarmRow> alarmHistory = [];
    [ObservableProperty] private AlarmRow? selectedAlarm;
    [ObservableProperty] private AlarmRow? selectedHistoricalAlarm;
    [ObservableProperty] private string fromText = DateTime.Now.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss");
    [ObservableProperty] private string toText = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-dd HH:mm:ss");
    [ObservableProperty] private DeviceRow? historyDevice;
    [ObservableProperty] private TagRow? historyTag;
    [ObservableProperty] private List<HistoryRow> historyRows = [];
    [ObservableProperty] private int replayPosition;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ReplayButton))] private bool replayRunning;
    public string ReplayButton => ReplayRunning ? "暂停回放" : "播放历史";
    public string ReplayTime => ReplayPosition > 0 && ReplayPosition <= HistoryRows.Count ? HistoryRows[ReplayPosition - 1].TimeText : "尚未回放";
    [ObservableProperty] private string querySummary = "选择点位和时间段后查询";
    [ObservableProperty] private CommunicationSettings communication = new();
    [ObservableProperty] private TagRow? selectedTag;
    [ObservableProperty] private TagRow tagEditor = new();
    [ObservableProperty] private List<RuleRow> rules = [];
    [ObservableProperty] private RuleRow? selectedRule;
    [ObservableProperty] private RuleRow ruleEditor = new();
    [ObservableProperty] private List<UserRow> users = [];
    [ObservableProperty] private UserRow? selectedUser;
    [ObservableProperty] private string editUsername = "";
    [ObservableProperty] private string editRole = "Operator";
    [ObservableProperty] private bool editEnabled = true;
    [ObservableProperty] private string controlMode = "Coil";
    [ObservableProperty] private ISeries[] historySeries = [];
    public ISeries[] TrendSeries { get; }
    public Axis[] TimeAxes { get; } = [new() { MinStep = 1000, Labeler = value => DateTimeOffset.FromUnixTimeMilliseconds((long)value).LocalDateTime.ToString("HH:mm:ss"), LabelsPaint = new SolidColorPaint(SKColor.Parse("#677D8A")), TextSize = 11 }];
    public Axis[] ValueAxes { get; } = [new() { LabelsPaint = new SolidColorPaint(SKColor.Parse("#677D8A")), TextSize = 11 }];

    public MainViewModel(StationRepository repository, StationRuntime runtime, StationFacade facade, SessionService session, ILogger<MainViewModel> logger)
    {
        this.repository = repository; this.runtime = runtime; this.facade = facade; this.session = session; this.logger = logger;
        TrendSeries = [new LineSeries<ObservablePoint> { Values = trendValues, Name = "实时值", GeometrySize = 0, Fill = null, Stroke = new SolidColorPaint(SKColor.Parse("#147D92"), 3), LineSmoothness = 0 }];
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) }; timer.Tick += OnTick;
    }
    public async Task InitializeAsync() { await RefreshConfigurationAsync(); await RefreshAlarmsAsync(); timer.Start(); }
    private async void OnTick(object? sender, EventArgs e)
    {
        if (refreshing) return; refreshing = true;
        try
        {
            if (ReplayRunning) { ReplayPosition = Math.Min(HistoryRows.Count, ReplayPosition + Math.Max(1, HistoryRows.Count / 200)); if (ReplayPosition >= HistoryRows.Count) ReplayRunning = false; }
            Clock = DateTime.Now.ToString("yyyy年MM月dd日  HH:mm:ss"); var snapshot = runtime.Snapshot;
            Connection = snapshot.Connection switch { Core.Protocol.ConnectionState.Connected => "已连接", Core.Protocol.ConnectionState.Reconnecting => "重连中", Core.Protocol.ConnectionState.Connecting => "连接中", _ => "已断开" };
            ConnectionColor = Connection == "已连接" ? "#19866C" : "#C06932";
            bool stale = (DateTimeOffset.UtcNow - snapshot.UpdatedAt).TotalMilliseconds > Math.Max(3000, Communication.PollIntervalMs * 3);
            if (lastPoll != snapshot.PollCount || stale)
            {
                bool newPoll = lastPoll != snapshot.PollCount; lastPoll = snapshot.PollCount;
                PollLabel = $"采集周期 {Communication.PollIntervalMs} ms  /  本轮 {snapshot.PollMilliseconds:F1} ms  /  {snapshot.PollCount:N0} 轮";
                foreach (var value in snapshot.Values)
                {
                    var row = LiveRows.FirstOrDefault(r => r.Id == value.Tag.Id); if (row is null) continue;
                    bool valueStale = stale || (DateTimeOffset.UtcNow - value.Timestamp).TotalMilliseconds > Math.Max(3000, Communication.PollIntervalMs * 3);
                    row.ValueText = value.Quality == DataQuality.Good && !valueStale ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "—";
                    row.Quality = valueStale ? "过期" : value.Quality == DataQuality.Good ? "良好" : "无效"; row.Updated = value.Timestamp.LocalDateTime.ToString("HH:mm:ss");
                    if (newPoll) { if (!trendBuffer.TryGetValue(value.Tag.Id, out var buffer)) trendBuffer[value.Tag.Id] = buffer = new(); buffer.Enqueue(value); while (buffer.Count > 36000 || (buffer.Count > 0 && value.Timestamp - buffer.Peek().Timestamp > TimeSpan.FromHours(1))) buffer.Dequeue(); }
                }
                UpdateCards(snapshot, stale); if (newPoll) RenderTrend();
            }
            if (++refreshCounter % 8 == 0) await RefreshAlarmsAsync();
        }
        catch (Exception ex) { logger.LogError(ex, "UI refresh failed"); Status = "刷新失败：" + ex.Message; }
        finally { refreshing = false; }
    }
    private void UpdateCards(StationSnapshot snapshot, bool stale)
    {
        var data = snapshot.Values.ToDictionary(v => v.Tag.Id);
        foreach (var card in Devices)
        {
            bool good = !stale && snapshot.Values.Any(v => v.Tag.DeviceId == card.Id) && snapshot.Values.Where(v => v.Tag.DeviceId == card.Id).All(v => v.Quality == DataQuality.Good && (DateTimeOffset.UtcNow - v.Timestamp).TotalMilliseconds <= Math.Max(3000, Communication.PollIntervalMs * 3));
            double V(string id) => data.GetValueOrDefault(id)?.Value ?? 0;
            if (!good) { card.Status = "数据不可用"; card.Color = "#7D8E9A"; card.Primary = "—"; card.Secondary = "检查通信连接"; card.Gauge = 0; continue; }
            if (card.Id.StartsWith("pump", StringComparison.Ordinal))
            {
                bool fault = V(card.Id + "_fault") != 0, run = V(card.Id + "_run") != 0; card.Status = fault ? "故障" : run ? "运行中" : "待机"; card.Color = fault ? "#CA4C41" : run ? "#19866C" : "#7D8E9A";
                card.Primary = V(card.Id + "_frequency").ToString("F1"); card.PrimaryUnit = "Hz"; card.Secondary = $"电流 {V(card.Id + "_current"):F1} A   累计 {V(card.Id + "_hours"):F0} h"; card.Gauge = V(card.Id + "_frequency") / 60 * 100;
            }
            else if (card.Id == "sump")
            {
                card.Status = V("high_switch") != 0 ? "高液位" : V("low_switch") != 0 ? "低液位" : "液位正常"; card.Color = card.Status == "液位正常" ? "#19866C" : "#CA4C41"; card.Primary = V("level").ToString("F2"); card.PrimaryUnit = "m"; card.Secondary = $"进水流量 {V("inflow"):F1} m³/h"; card.Gauge = V("level") / 10 * 100;
            }
            else { card.Status = "监测中"; card.Color = "#19866C"; card.Primary = V("pressure").ToString("F3"); card.PrimaryUnit = "MPa"; card.Secondary = $"流量 {V("outflow"):F1} m³/h   阀门 {V("valve"):F0}%"; card.Gauge = V("valve"); }
        }
    }
    partial void OnTrendTagChanged(TagRow? value) => RenderTrend();
    partial void OnSpanMinutesChanged(int value) => RenderTrend();
    partial void OnSelectedTagChanged(TagRow? value) { OnPropertyChanged(nameof(IsEditingTag)); if (value is not null) TagEditor = Clone(value); }
    partial void OnSelectedRuleChanged(RuleRow? value) { OnPropertyChanged(nameof(IsEditingRule)); if (value is not null) RuleEditor = Clone(value); }
    partial void OnSelectedUserChanged(UserRow? value) { if (value is not null) { EditUsername = value.Username; EditRole = value.Role; EditEnabled = value.Enabled; } }
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private void RenderTrend()
    {
        trendValues.Clear(); if (TrendTag is null || !trendBuffer.TryGetValue(TrendTag.Id, out var buffer)) return;
        var points = buffer.Where(v => v.Timestamp >= DateTimeOffset.UtcNow.AddMinutes(-SpanMinutes)).ToArray(); int stride = Math.Max(1, (int)Math.Ceiling(points.Length / 1500.0));
        for (int i = 0; i < points.Length; i += stride) { var p = points[i]; trendValues.Add(new(p.Timestamp.ToUnixTimeMilliseconds(), p.Quality == DataQuality.Good ? p.Value : null)); }
    }
    private async Task RefreshConfigurationAsync()
    {
        Tags = await repository.TagsAsync(); DeviceOptions = [new() { Id = "", Name = "全部设备" }, .. await repository.DevicesAsync()]; Rules = await repository.RulesAsync(); Communication = await repository.SettingsAsync();
        Users = IsAdmin ? (await repository.UsersAsync()).Select(u => new UserRow { Username = u.Username, Role = u.Role, Enabled = u.Enabled }).ToList() : [];
        string? selected = TrendTag?.Id; TrendTag = Tags.FirstOrDefault(t => t.Id == (selected ?? "level")); HistoryTag ??= Tags.FirstOrDefault(t => t.Id == "level"); HistoryDevice ??= DeviceOptions[0];
        LiveRows.Clear(); foreach (var tag in Tags.Where(t => t.Enabled)) LiveRows.Add(new(tag)); lastPoll = -1;
        foreach (var id in trendBuffer.Keys.Where(id => Tags.All(t => t.Id != id)).ToArray()) trendBuffer.Remove(id);
    }
    private async Task RefreshAlarmsAsync() { string? selectedId=SelectedAlarm?.Id; ActiveAlarms = await repository.AlarmsAsync(activeOnly: true); SelectedAlarm=ActiveAlarms.FirstOrDefault(a=>a.Id==selectedId); ActiveCount = ActiveAlarms.Count; AlarmSummary = ActiveCount == 0 ? "无活动报警" : $"{ActiveCount} 条活动报警 · {ActiveAlarms.Count(a => a.AcknowledgedAt is null)} 条未确认"; }
    private async Task RunAsync(Func<Task> action, string success)
    {
        try { await action(); Status = success; }
        catch (Exception ex) { logger.LogError(ex, "User action failed"); Status = ex.Message; }
    }
    private (long From, long To) Range()
    {
        if (!DateTime.TryParseExact(FromText, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) || !DateTime.TryParseExact(ToText, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) || from > to) throw new ArgumentException("时间格式为 yyyy-MM-dd HH:mm:ss，结束时间不能早于开始时间");
        return (new DateTimeOffset(from).ToUnixTimeMilliseconds(), new DateTimeOffset(to).ToUnixTimeMilliseconds());
    }
    [RelayCommand]
    private async Task QueryHistoryAsync() => await RunAsync(async () =>
    {
        var (from, to) = Range(); HistoryRows = await repository.QueryHistoryAsync(HistoryDevice?.Id, HistoryTag?.Id, from, to);
        ReplayRunning = false; ReplayPosition = HistoryRows.Count; RenderHistory();
        QuerySummary = $"查询到 {HistoryRows.Count:N0} 条记录" + (HistoryRows.Count == 10000 ? "（达到 10,000 条上限，请缩小时间段）" : "");
    }, "历史查询完成");
    partial void OnReplayPositionChanged(int value) { RenderHistory(); OnPropertyChanged(nameof(ReplayTime)); }
    private void RenderHistory()
    {
        int count = Math.Clamp(ReplayPosition, 0, HistoryRows.Count), stride = Math.Max(1, (int)Math.Ceiling(count / 2000.0));
        HistorySeries = [new LineSeries<ObservablePoint> { Name = HistoryTag?.Name ?? "历史值", Values = HistoryRows.Take(count).Where((_, i) => i % stride == 0).Select(r => new ObservablePoint(r.Timestamp, r.Quality == "Good" ? r.Value : null)).ToArray(), GeometrySize = 0, Fill = null, Stroke = new SolidColorPaint(SKColor.Parse("#147D92"), 2), LineSmoothness = 0 }];
    }
    [RelayCommand] private void ToggleReplay() { if (HistoryRows.Count == 0) { Status = "请先查询历史记录"; return; } if (ReplayPosition >= HistoryRows.Count) ReplayPosition = 0; ReplayRunning = !ReplayRunning; }
    public async Task RefreshSessionAsync() { OnPropertyChanged(nameof(IsAdmin)); OnPropertyChanged(nameof(UserLabel)); await RefreshConfigurationAsync(); }
    [RelayCommand] private async Task QueryAlarmsAsync() => await RunAsync(async () => { var (from, to) = Range(); AlarmHistory = await repository.AlarmsAsync(from, to); }, "报警历史已刷新（最多 10,000 条）");
    [RelayCommand] private async Task AcknowledgeAsync() => await RunAsync(async () => { if (SelectedAlarm is null) throw new InvalidOperationException("请先选择当前报警"); await facade.AcknowledgeAsync(SelectedAlarm.Id); await RefreshAlarmsAsync(); }, "报警已确认");
    [RelayCommand] private async Task AcknowledgeHistoryAsync() => await RunAsync(async () => { if (SelectedHistoricalAlarm is null) throw new InvalidOperationException("请先选择历史报警"); await facade.AcknowledgeAsync(SelectedHistoricalAlarm.Id); await QueryAlarmsAsync(); }, "报警已确认");
    [RelayCommand]
    private async Task ExportAlarmsAsync() => await RunAsync(async () =>
    {
        var (from, to) = Range(); var dialog = new SaveFileDialog { Filter = "CSV 文件|*.csv", FileName = $"报警记录-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog() == true) await facade.ExportAlarmsAsync(dialog.FileName, from, to);
    }, "导出操作结束");
    [RelayCommand] private async Task ControlAsync(string parameter) => await RunAsync(async () => { var parts = parameter.Split(':'); await facade.ControlAsync(int.Parse(parts[0], CultureInfo.InvariantCulture), parts[1] == "start", ControlMode); }, "控制命令已应答，状态将在下一采集周期更新");
    [RelayCommand] private async Task SaveCommunicationAsync() => await RunAsync(async () => { await facade.SaveSettingsAsync(Communication); await RefreshConfigurationAsync(); }, "通信参数已保存并生效");
    [RelayCommand] private void NewTag() { SelectedTag = null; TagEditor = new() { Id = "tag_" + DateTime.Now.ToString("HHmmss") }; }
    [RelayCommand] private async Task SaveTagAsync() => await RunAsync(async () => { await facade.SaveTagAsync(TagEditor); await RefreshConfigurationAsync(); }, "点位已保存");
    [RelayCommand] private async Task DeleteTagAsync() => await RunAsync(async () => { if (SelectedTag is null) throw new InvalidOperationException("请选择点位"); await facade.DeleteTagAsync(SelectedTag.Id); await RefreshConfigurationAsync(); }, "点位已删除，历史记录保留");
    [RelayCommand] private void NewRule() { SelectedRule = null; RuleEditor = new() { Id = "rule_" + DateTime.Now.ToString("HHmmss") }; }
    [RelayCommand] private async Task SaveRuleAsync() => await RunAsync(async () => { await facade.SaveRuleAsync(RuleEditor); await RefreshConfigurationAsync(); }, "报警规则已保存");
    [RelayCommand] private async Task DeleteRuleAsync() => await RunAsync(async () => { if (SelectedRule is null) throw new InvalidOperationException("请选择规则"); await facade.DeleteRuleAsync(SelectedRule.Id); await RefreshConfigurationAsync(); }, "报警规则已删除");
    [RelayCommand] private void NewUser() { SelectedUser = null; EditUsername = ""; EditRole = "Operator"; EditEnabled = true; }
    [RelayCommand] private async Task SaveUserAsync(string password) => await RunAsync(async () => { await facade.SaveUserAsync(EditUsername, password, EditRole, EditEnabled); await RefreshConfigurationAsync(); OnPropertyChanged(nameof(IsAdmin)); OnPropertyChanged(nameof(UserLabel)); }, "用户已保存");
    [RelayCommand] private async Task DeleteUserAsync() => await RunAsync(async () => { if (SelectedUser is null) throw new InvalidOperationException("请选择用户"); await facade.DeleteUserAsync(SelectedUser.Username); await RefreshConfigurationAsync(); }, "用户已删除");
    public void Dispose() { timer.Stop(); timer.Tick -= OnTick; }
}
