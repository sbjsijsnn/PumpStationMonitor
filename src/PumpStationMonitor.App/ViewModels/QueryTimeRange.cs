using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PumpStationMonitor.App.ViewModels;

// 保存报警历史与采样历史共用的查询起止时间。
// 用同一套本地时间解析规则，避免页面拆分后产生时间解释差异。
// 查询时转换为统一的毫秒时间戳，方便数据库比较。
public partial class QueryTimeRange : ObservableObject
{
    [ObservableProperty]
    private string fromText = DateTime.Now.AddHours(-1).ToString("yyyy-MM-dd HH:mm:ss");
    [ObservableProperty]
    private string toText = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-dd HH:mm:ss");
    public (long From, long To) Parse()
    {
        const string format = "yyyy-MM-dd HH:mm:ss";
        bool validFrom = DateTime.TryParseExact(
            FromText, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var from);
        bool validTo = DateTime.TryParseExact(
            ToText, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var to);
        if (!validFrom || !validTo || from > to)
        {
            throw new ArgumentException("时间格式为 yyyy-MM-dd HH:mm:ss，结束时间不能早于开始时间");
        }

        long fromMilliseconds = new DateTimeOffset(from).ToUnixTimeMilliseconds();
        long toMilliseconds = new DateTimeOffset(to).ToUnixTimeMilliseconds();
        return (fromMilliseconds, toMilliseconds);
    }
}
