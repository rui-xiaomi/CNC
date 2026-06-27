using CncLoader.UI.Navigation;
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

        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<DashboardViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<WorkLineViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<CraftworkViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<EquipmentViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<PlcViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<AgvViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<ScanViewModel>());
        services.AddSingleton<PageViewModelBase>(sp => sp.GetRequiredService<FrameViewModel>());

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<ShellWindow>();

        return services;
    }
}
