using System.Windows;
using PumpStationMonitor.App.ViewModels;

namespace PumpStationMonitor.App.Views;

// 呈现账号与密码输入并衔接登录命令。
// 密码从密码框短暂传递给视图模型，不作为可观察字符串长期保存。
// 视图只处理控件交互，验证交由会话服务完成。
public partial class LoginWindow : Window
{
    public LoginWindow(LoginViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void LoginClick(object sender, RoutedEventArgs e)
    {
        var model = (LoginViewModel)DataContext;
        if (model.LoginCommand.IsRunning)
        {
            return;
        }

        await model.LoginCommand.ExecuteAsync(Password.Password);
        Password.Clear();
    }
}
