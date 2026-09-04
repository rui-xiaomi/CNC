using System.Reflection;
using CncLoader.Core.Abstractions;
using CncLoader.UI.Services;
using CncLoader.UI.ViewModels;
using CncLoader.UI.ViewModels.Pages;

namespace CncLoader.Core.Tests.UI;

/// <summary>
/// ViewModel 与视觉树之间的接缝边界：所有 VM 只能经注入的
/// <see cref="IUserNotificationService"/> / <see cref="IUiDispatcher"/> / <see cref="IDialogService"/>
/// 触达 UI，不得直接依赖 HandyControl Growl、MessageBox、Dispatcher 或具体 Window。
/// 这条边界一旦破，VM 在 headless 下就不可构造，命令逻辑也随之失去覆盖。
/// </summary>
[TestFixture]
public sealed class UiSeamBoundaryTests
{
    private static readonly Type[] ViewModels =
    {
        typeof(ShellViewModel),
        typeof(DashboardViewModel),
        typeof(WorkLineViewModel),
        typeof(CraftworkViewModel),
        typeof(EquipmentViewModel),
        typeof(PlcViewModel),
        typeof(PointMappingViewModel),
        typeof(AgvViewModel),
        typeof(ScanViewModel),
        typeof(FrameViewModel),
        typeof(RcsViewModel),
        typeof(LogViewModel)
    };

    [Test]
    public void ViewModels_DoNotReferenceConcreteDialogWindows()
    {
        var offenders = ViewModels
            .SelectMany(vm => vm.GetConstructors().SelectMany(c => c.GetParameters()))
            .Where(p => p.ParameterType.Namespace == "CncLoader.UI.Views.Dialogs"
                        || typeof(System.Windows.Window).IsAssignableFrom(p.ParameterType))
            .Select(p => $"{p.Member.DeclaringType!.Name}.{p.Name}")
            .ToList();

        Assert.That(offenders, Is.Empty,
            "ViewModel 不得注入具体 Window/Dialog；模态编辑一律经 IDialogService");
    }

    [Test]
    public void ViewModels_ThatNotifyUser_TakeNotificationSeam()
    {
        // 这些 VM 都有需要提示操作员的命令；缺少接缝就说明又直调了静态 Growl/MessageBox
        var needNotify = new[]
        {
            typeof(ShellViewModel), typeof(DashboardViewModel), typeof(WorkLineViewModel),
            typeof(CraftworkViewModel), typeof(EquipmentViewModel), typeof(PlcViewModel),
            typeof(PointMappingViewModel), typeof(AgvViewModel), typeof(ScanViewModel),
            typeof(FrameViewModel), typeof(RcsViewModel), typeof(LogViewModel)
        };

        Assert.Multiple(() =>
        {
            foreach (var vm in needNotify)
            {
                Assert.That(TakesParameter(vm, typeof(IUserNotificationService)), Is.True,
                    $"{vm.Name} 须注入 IUserNotificationService");
            }
        });
    }

    [Test]
    public void ViewModels_ThatTouchCollectionsFromBackground_TakeDispatcherSeam()
    {
        // 订阅后台事件（PLC 连接、RCS 回调、告警、扫码、信号仓）并改 ObservableCollection 的 VM
        var needDispatcher = new[]
        {
            typeof(ShellViewModel), typeof(DashboardViewModel), typeof(PlcViewModel),
            typeof(AgvViewModel), typeof(ScanViewModel), typeof(FrameViewModel),
            typeof(RcsViewModel), typeof(LogViewModel)
        };

        Assert.Multiple(() =>
        {
            foreach (var vm in needDispatcher)
            {
                Assert.That(TakesParameter(vm, typeof(IUiDispatcher)), Is.True,
                    $"{vm.Name} 须注入 IUiDispatcher（禁止直接摸 Application.Current.Dispatcher）");
            }
        });
    }

    [Test]
    public void ViewModels_ThatOpenEditDialogs_TakeDialogSeam()
    {
        var needDialogs = new[]
        {
            typeof(PlcViewModel), typeof(EquipmentViewModel), typeof(FrameViewModel)
        };

        Assert.Multiple(() =>
        {
            foreach (var vm in needDialogs)
            {
                Assert.That(TakesParameter(vm, typeof(IDialogService)), Is.True,
                    $"{vm.Name} 须注入 IDialogService");
            }
        });
    }

    [Test]
    public void NotificationSeam_CoversBlockingPrompts_NotJustToasts()
    {
        // 二次确认与阻断式告警必须也在接缝内，否则危险操作确认无法断言
        var members = typeof(IUserNotificationService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ToList();

        Assert.That(members, Is.SupersetOf(new[] { "Success", "Info", "Warning", "Error", "Confirm", "Alert" }));
    }

    private static bool TakesParameter(Type type, Type parameterType)
        => type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Any(p => p.ParameterType == parameterType);
}
