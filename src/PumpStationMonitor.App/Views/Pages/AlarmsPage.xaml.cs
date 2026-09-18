using System.Windows;
using System.Windows.Controls;
using PumpStationMonitor.App.ViewModels;

namespace PumpStationMonitor.App.Views.Pages;

// 承载报警页面的独立控件树。
// 展示绑定到对应页面视图模型，布局修改不再影响整个主窗口。
// 代码后置只处理控件初始化，业务操作通过绑定命令执行。
public partial class AlarmsPage : UserControl
{
    public AlarmsPage()
    {
        InitializeComponent();
    }
}
