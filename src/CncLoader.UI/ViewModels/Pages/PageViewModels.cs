namespace CncLoader.UI.ViewModels.Pages;

// 仍为占位的页面 ViewModel（监控看板为静态骨架，后续接线实时数据）。
// AGV/扫码枪 全功能 ViewModel 见 AgvViewModel.cs / ScanViewModel.cs。

public sealed class DashboardViewModel : PageViewModelBase
{
    public override string Key => "dash";
    public override string Title => "监控看板";
}
