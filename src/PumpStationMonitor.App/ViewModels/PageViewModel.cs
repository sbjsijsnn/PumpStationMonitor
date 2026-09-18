using CommunityToolkit.Mvvm.ComponentModel;

namespace PumpStationMonitor.App.ViewModels;

// 提供页面通用的上下文访问、权限通知和命令错误处理。
// 复用跨页面的一致操作反馈，具体业务状态仍由子类各自拥有。
// 页面与主窗口无继承关系，拆分后可以独立阅读与维护。
public abstract class PageViewModel : ObservableObject
{
    public PageContext Context
    {
        get;
    }
    protected PumpStationMonitor.Data.StationRepository repository => Context.Repository;
    protected PumpStationMonitor.Data.StationFacade facade => Context.Facade;
    public bool IsAdmin => Context.IsAdmin;
    protected string Status
    {
        get => Context.Status;
        set => Context.Status = value;
    }

    protected PageViewModel(PageContext context)
    {
        Context = context;
        context.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PageContext.IsAdmin))
            {
                OnPropertyChanged(nameof(IsAdmin));
            }
        };
    }

    protected Task RunAsync(Func<Task> action, string success) => Context.RunAsync(action, success);
}
