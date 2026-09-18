using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PumpStationMonitor.Core.Services;

namespace PumpStationMonitor.App.ViewModels;

// 处理当前报警、历史报警、确认和导出操作。
// 活动列表刷新时保留选中项，方便操作员持续查看同一条报警。
// 确认与导出通过业务入口执行，查询时间与历史页面共用。
public partial class AlarmsPageViewModel : PageViewModel
{
    [ObservableProperty]
    private int activeCount;
    [ObservableProperty]
    private string alarmSummary = "无活动报警";
    [ObservableProperty]
    private List<AlarmRow> activeAlarms = [];
    [ObservableProperty]
    private List<AlarmRow> alarmHistory = [];
    [ObservableProperty]
    private AlarmRow? selectedAlarm;
    [ObservableProperty]
    private AlarmRow? selectedHistoricalAlarm;
    public async Task RefreshAlarmsAsync()
    {
        string? selectedId = SelectedAlarm?.Id;
        ActiveAlarms = await repository.AlarmsAsync(activeOnly: true);
        SelectedAlarm = ActiveAlarms.FirstOrDefault(a => a.Id == selectedId);
        ActiveCount = ActiveAlarms.Count;
        AlarmSummary = ActiveCount == 0
            ? "无活动报警"
            : $"{ActiveCount} 条活动报警 · {ActiveAlarms.Count(a => a.AcknowledgedAt is null)} 条未确认";
    }

    [RelayCommand]
    private async Task QueryAlarmsAsync() => await RunAsync(
        async () =>
        {
            var (from, to) = Range();
            AlarmHistory = await repository.AlarmsAsync(from, to);
        },
        "报警历史已刷新（最多 10,000 条）"
    );
    [RelayCommand]
    private async Task AcknowledgeAsync() => await RunAsync(
        async () =>
        {
            if (SelectedAlarm is null)
            {
                throw new InvalidOperationException("请先选择当前报警");
            }

            await facade.AcknowledgeAsync(SelectedAlarm.Id);
            await RefreshAlarmsAsync();
        },
        "报警已确认"
    );
    [RelayCommand]
    private async Task AcknowledgeHistoryAsync() => await RunAsync(
        async () =>
        {
            if (SelectedHistoricalAlarm is null)
            {
                throw new InvalidOperationException("请先选择历史报警");
            }

            await facade.AcknowledgeAsync(SelectedHistoricalAlarm.Id);
            await QueryAlarmsAsync();
        },
        "报警已确认"
    );
    [RelayCommand]
    private async Task ExportAlarmsAsync() => await RunAsync(
        async () =>
        {
            var (from, to) = Range();
            var dialog = new SaveFileDialog
            {
                Filter = "CSV 文件|*.csv",
                FileName = $"报警记录-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
            };
            if (dialog.ShowDialog() == true)
            {
                await facade.ExportAlarmsAsync(dialog.FileName, from, to);
            }
        },
        "导出操作结束"
    );
    public QueryTimeRange TimeRange => Context.TimeRange;

    private (long From, long To) Range() => TimeRange.Parse();
    public AlarmsPageViewModel(PageContext context) : base(context)
    {
    }
}
