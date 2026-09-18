using System.Collections.ObjectModel;
using System.Globalization;
using PumpStationMonitor.Core.Models;
using PumpStationMonitor.Core.Services;

namespace PumpStationMonitor.App.ViewModels;

// 维护实时点位表格中的数值、质量和更新时间。
// 每轮只更新已有行，让界面绑定保持稳定。
// 坏质量或过期值统一显示占位符，避免旧数据被误读为正常值。
public partial class RealtimePageViewModel : PageViewModel
{
    public ObservableCollection<LiveRow> LiveRows { get; } = [];

    public RealtimePageViewModel(PageContext context) : base(context)
    {
    }

    public void Configure(IEnumerable<TagRow> tags)
    {
        LiveRows.Clear();
        foreach (var tag in tags.Where(t => t.Enabled))
        {
            LiveRows.Add(new(tag));
        }
    }

    public void Refresh(StationSnapshot snapshot, bool stale)
    {
        foreach (var value in snapshot.Values)
        {
            var row = LiveRows.FirstOrDefault(r => r.Id == value.Tag.Id);
            if (row is null)
            {
                continue;
            }

            double age = (DateTimeOffset.UtcNow - value.Timestamp).TotalMilliseconds;
            bool valueStale = stale || age > Math.Max(3000, Context.Communication.PollIntervalMs * 3);
            bool good = value.Quality == DataQuality.Good && !valueStale;
            row.ValueText = good
                ? value.Value.ToString("0.###", CultureInfo.InvariantCulture)
                : "—";
            row.Quality = valueStale ? "过期" : value.Quality == DataQuality.Good ? "良好" : "无效";
            row.Updated = value.Timestamp.LocalDateTime.ToString("HH:mm:ss");
        }
    }
}
