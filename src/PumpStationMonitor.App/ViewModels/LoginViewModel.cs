using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PumpStationMonitor.Data.Security;

namespace PumpStationMonitor.App.ViewModels;

// 管理登录表单、错误提示和登录成功通知。
// 验证委托给会话服务，界面不接触密码散列和数据库规则。
// 成功事件由应用入口处理，从而分离验证与窗口切换。
public partial class LoginViewModel(SessionService session, ILogger<LoginViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    private string username = "admin";
    [ObservableProperty]
    private string error = "";
    public event Action? SignedIn;
    [RelayCommand]
    private async Task LoginAsync(string password)
    {
        try
        {
            Error = "";
            if (await session.LoginAsync(Username, password))
            {
                SignedIn?.Invoke();
            }
            else
            {
                Error = "用户名或密码不正确";
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Login failed");
            Error = ex.Message;
        }
    }
}
