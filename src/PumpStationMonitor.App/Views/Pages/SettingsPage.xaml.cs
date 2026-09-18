using System.Windows;
using System.Windows.Controls;
using PumpStationMonitor.App.ViewModels;

namespace PumpStationMonitor.App.Views.Pages;

// 承载设置页面的独立控件树。
// 展示绑定到对应页面视图模型，布局修改不再影响整个主窗口。
// 代码后置只处理控件初始化与密码框传递。
public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private async void SaveUserClick(object sender, RoutedEventArgs e)
    {
        var model = (SettingsPageViewModel)DataContext;
        if (model.SaveUserCommand.IsRunning)
        {
            return;
        }

        await model.SaveUserCommand.ExecuteAsync(UserPassword.Password);
        UserPassword.Clear();
    }
}
