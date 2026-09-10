using System.Windows;
using PumpStationMonitor.App.ViewModels;
namespace PumpStationMonitor.App.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel model) { InitializeComponent(); DataContext = model; }
    private async void SaveUserClick(object sender, RoutedEventArgs e)
    {
        var model = (MainViewModel)DataContext; if (model.SaveUserCommand.IsRunning) return;
        await model.SaveUserCommand.ExecuteAsync(UserPassword.Password); UserPassword.Clear();
    }
}
