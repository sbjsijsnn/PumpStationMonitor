using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PumpStationMonitor.Data.Security;
namespace PumpStationMonitor.App.ViewModels;

public partial class LoginViewModel(SessionService session, ILogger<LoginViewModel> logger) : ObservableObject
{
    [ObservableProperty] private string username = "admin";
    [ObservableProperty] private string error = "";
    public event Action? SignedIn;
    [RelayCommand]
    private async Task LoginAsync(string password)
    {
        try { Error = ""; if (await session.LoginAsync(Username, password)) SignedIn?.Invoke(); else Error = "用户名或密码不正确"; }
        catch (Exception ex) { logger.LogError(ex, "Login failed"); Error = ex.Message; }
    }
}
