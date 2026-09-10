using System.Windows;
using PumpStationMonitor.App.ViewModels;
namespace PumpStationMonitor.App.Views;

public partial class LoginWindow : Window
{
    public LoginWindow(LoginViewModel viewModel) { InitializeComponent(); DataContext = viewModel; }
    private async void LoginClick(object sender, RoutedEventArgs e)
    {
        var model = (LoginViewModel)DataContext; if (model.LoginCommand.IsRunning) return;
        await model.LoginCommand.ExecuteAsync(Password.Password); Password.Clear();
    }
}
