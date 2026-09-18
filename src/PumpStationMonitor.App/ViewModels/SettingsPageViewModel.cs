using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PumpStationMonitor.Core.Services;

namespace PumpStationMonitor.App.ViewModels;

// 管理通信、点位、报警规则和用户的编辑状态与命令。
// 选中记录复制到编辑对象，未保存的修改不会直接影响列表。
// 保存交给业务层校验，再通知所有页面刷新配置。
public partial class SettingsPageViewModel : PageViewModel
{
    public string[] Modes { get; } = ["TCP", "RTU"];
    public string[] Roles { get; } = ["Admin", "Operator"];
    public string[] Types { get; } = ["UInt16", "Int16", "Float32"];
    public string[] Orders { get; } = ["Abcd", "Badc", "Cdab", "Dcba"];
    public string[] Kinds { get; } = ["Bit", "HH", "H", "L", "LL"];
    public string[] Severities { get; } = ["Emergency", "Warning", "Information"];
    public IEnumerable<DeviceRow> StationDevices => DeviceOptions.Where(d => d.Id.Length > 0);
    public bool IsEditingTag => SelectedTag is not null;
    public bool IsEditingRule => SelectedRule is not null;

    [ObservableProperty]
    private List<TagRow> tags = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StationDevices))]
    private List<DeviceRow> deviceOptions = [];
    [ObservableProperty]
    private CommunicationSettings communication = new();
    [ObservableProperty]
    private TagRow? selectedTag;
    [ObservableProperty]
    private TagRow tagEditor = new();
    [ObservableProperty]
    private List<RuleRow> rules = [];
    [ObservableProperty]
    private RuleRow? selectedRule;
    [ObservableProperty]
    private RuleRow ruleEditor = new();
    [ObservableProperty]
    private List<UserRow> users = [];
    [ObservableProperty]
    private UserRow? selectedUser;
    [ObservableProperty]
    private string editUsername = "";
    [ObservableProperty]
    private string editRole = "Operator";
    [ObservableProperty]
    private bool editEnabled = true;
    partial void OnSelectedTagChanged(TagRow? value)
    {
        OnPropertyChanged(nameof(IsEditingTag));
        if (value is not null)
        {
            TagEditor = Clone(value);
        }
    }

    partial void OnSelectedRuleChanged(RuleRow? value)
    {
        OnPropertyChanged(nameof(IsEditingRule));
        if (value is not null)
        {
            RuleEditor = Clone(value);
        }
    }

    partial void OnSelectedUserChanged(UserRow? value)
    {
        if (value is not null)
        {
            EditUsername = value.Username;
            EditRole = value.Role;
            EditEnabled = value.Enabled;
        }
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    [RelayCommand]
    private async Task SaveCommunicationAsync() => await RunAsync(
        async () =>
        {
            await facade.SaveSettingsAsync(Communication);
            await Context.RefreshConfigurationAsync();
        },
        "通信参数已保存并生效"
    );
    [RelayCommand]
    private void NewTag()
    {
        SelectedTag = null;
        TagEditor = new()
        {
            Id = "tag_" + DateTime.Now.ToString("HHmmss")
        };
    }

    [RelayCommand]
    private async Task SaveTagAsync() => await RunAsync(
        async () =>
        {
            await facade.SaveTagAsync(TagEditor);
            await Context.RefreshConfigurationAsync();
        },
        "点位已保存"
    );
    [RelayCommand]
    private async Task DeleteTagAsync() => await RunAsync(
        async () =>
        {
            if (SelectedTag is null)
            {
                throw new InvalidOperationException("请选择点位");
            }

            await facade.DeleteTagAsync(SelectedTag.Id);
            await Context.RefreshConfigurationAsync();
        },
        "点位已删除，历史记录保留"
    );
    [RelayCommand]
    private void NewRule()
    {
        SelectedRule = null;
        RuleEditor = new()
        {
            Id = "rule_" + DateTime.Now.ToString("HHmmss")
        };
    }

    [RelayCommand]
    private async Task SaveRuleAsync() => await RunAsync(
        async () =>
        {
            await facade.SaveRuleAsync(RuleEditor);
            await Context.RefreshConfigurationAsync();
        },
        "报警规则已保存"
    );
    [RelayCommand]
    private async Task DeleteRuleAsync() => await RunAsync(
        async () =>
        {
            if (SelectedRule is null)
            {
                throw new InvalidOperationException("请选择规则");
            }

            await facade.DeleteRuleAsync(SelectedRule.Id);
            await Context.RefreshConfigurationAsync();
        },
        "报警规则已删除"
    );
    [RelayCommand]
    private void NewUser()
    {
        SelectedUser = null;
        EditUsername = "";
        EditRole = "Operator";
        EditEnabled = true;
    }

    [RelayCommand]
    private async Task SaveUserAsync(string password) => await RunAsync(
        async () =>
        {
            await facade.SaveUserAsync(EditUsername, password, EditRole, EditEnabled);
            await Context.RefreshSessionAsync();
        },
        "用户已保存"
    );
    [RelayCommand]
    private async Task DeleteUserAsync() => await RunAsync(
        async () =>
        {
            if (SelectedUser is null)
            {
                throw new InvalidOperationException("请选择用户");
            }

            await facade.DeleteUserAsync(SelectedUser.Username);
            await Context.RefreshConfigurationAsync();
        },
        "用户已删除"
    );
    public SettingsPageViewModel(PageContext context) : base(context)
    {
    }

    public async Task ConfigureAsync(List<TagRow> availableTags, List<DeviceRow> devices)
    {
        Tags = availableTags;
        DeviceOptions = devices;
        Rules = await repository.RulesAsync();
        Communication = Context.Communication;
        Users = IsAdmin
            ? (await repository.UsersAsync()).Select(
            u => new UserRow
            {
                Username = u.Username,
                Role = u.Role,
                Enabled = u.Enabled
            }
        ).ToList()
            : [];
    }
}
