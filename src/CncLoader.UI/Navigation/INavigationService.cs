using CncLoader.UI.ViewModels;

namespace CncLoader.UI.Navigation;

/// <summary>页面导航服务：按导航键切换当前页面 ViewModel（ContentControl 路由，非树形）。</summary>
public interface INavigationService
{
    PageViewModelBase? Current { get; }
    event EventHandler<PageViewModelBase>? Navigated;
    void NavigateTo(string key);
}
