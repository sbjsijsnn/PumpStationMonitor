using Microsoft.Extensions.Logging;
using PumpStationMonitor.Core.Services;

namespace PumpStationMonitor.Data.Security;

// 验证登录并保存当前会话身份。
// 角色权限在业务请求前重新检查，避免界面禁用成为唯一约束。
// 密码验证委托专门散列工具，服务只管理身份和授权。
public sealed class SessionService(StationRepository repository, ILogger<SessionService> logger)
{
    public string? Username
    {
        get;
        private set;
    }
    public string? Role
    {
        get;
        private set;
    }

    private int failures;
    private DateTimeOffset retryAfter;
    public bool IsAdmin => Role == "Admin";

    public async Task<bool> LoginAsync(string name, string password)
    {
        if (DateTimeOffset.UtcNow < retryAfter)
        {
            throw new InvalidOperationException("登录尝试过多，请 30 秒后重试");
        }

        var user = await repository.UserAsync(name.Trim());
        bool valid = user is { Enabled: true } && PasswordHasher.Verify(password, user.PasswordHash);
        if (!valid)
        {
            failures++;
            if (failures >= 5)
            {
                retryAfter = DateTimeOffset.UtcNow.AddSeconds(30);
            }

            logger.LogWarning("Login rejected for {Name}", name);
            return false;
        }

        failures = 0;
        Username = user!.Username;
        Role = user.Role;
        logger.LogInformation("User {Name} signed in as {Role}", Username, Role);
        return true;
    }

    public void Logout()
    {
        logger.LogInformation("User {Name} signed out", Username);
        Username = null;
        Role = null;
    }

    public async Task RequireAsync(bool admin = false)
    {
        var user = Username is null ? null : await repository.UserAsync(Username);
        if (user is not { Enabled: true } || (admin && user.Role != "Admin"))
        {
            throw new UnauthorizedAccessException(admin ? "此操作仅管理员可执行" : "请先登录");
        }

        Role = user.Role;
    }
}
