namespace CncLoader.UI.ViewModels.Pages;

// 仍为占位的页面 ViewModel（监控看板为静态骨架；AGV/扫码枪 为 Phase 5）。
// 线体/工序/机台/料架 的全功能 ViewModel 见各自独立文件。

public sealed class DashboardViewModel : PageViewModelBase
{
    public override string Key => "dash";
    public override string Title => "监控看板";
}

public sealed class AgvViewModel : PageViewModelBase
{
    public override string Key => "agv";
    public override string Title => "AGV 管理";
}

public sealed class ScanViewModel : PageViewModelBase
{
    public override string Key => "scan";
    public override string Title => "扫码枪管理";
}
