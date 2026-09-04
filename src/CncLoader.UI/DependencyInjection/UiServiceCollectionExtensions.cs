using CncLoader.Core.Abstractions;
using CncLoader.UI.Navigation;
using CncLoader.UI.Services;
using CncLoader.UI.ViewModels;
using CncLoader.UI.ViewModels.Pages;
using CncLoader.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace CncLoader.UI.DependencyInjection;

public static class UiServiceCollectionExtensions
{
    /// <summary>注册 UI 层：页面 ViewModel、导航服务、外壳与主窗口。</summary>
    public static IServiceCollection AddCncUi(this IServiceCollection services)
    {
        // ViewModel 与视觉树之间的三个接缝：通知 / UI 线程 / 模态对话框。
        // VM 只依赖这三个接口，不再直接摸 Growl、MessageBox、Dispatcher 或 new Window。
        services.AddSingleton<IUserNotificationService, HandyControlUserNotificationService>();
        services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
        services.AddSingleton<IDialogService, WpfDialogService>();

        // 8 个页面 ViewModel（同时以 PageViewModelBase 暴露给导航服务）
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<WorkLineViewModel>();
        services.AddSingleton<CraftworkViewModel>();
        services.AddSingleton<EquipmentViewModel>();
        services.AddSingleton<PlcViewModel>();
        services.AddSingleton<PointMappingViewModel>();
        services.AddSingleton<AgvViewModel>();
        services.AddSingleton<ScanViewModel>();
        services.AddSingleton<FrameViewModel>();
        services.AddSingleton<RcsViewModel>();
        services.AddSingleton<LogViewModel>();

        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<DashboardViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<WorkLineViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<CraftworkViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<EquipmentViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<PlcViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<AgvViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<ScanViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<FrameViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<RcsViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<LogViewModel>());

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<ShellWindow>();

        return services;
    }
}
