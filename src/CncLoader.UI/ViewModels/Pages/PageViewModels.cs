namespace CncLoader.UI.ViewModels.Pages;

// Phase 1：8 个页面 ViewModel 占位。导航/路由/标题联动可用；具体功能在后续阶段实现。
// PLC 管理为 Phase 2 优先模块。

public sealed class DashboardViewModel : PageViewModelBase
{
    public override string Key => "dash";
    public override string Title => "监控看板";
}

public sealed class WorkLineViewModel : PageViewModelBase
{
    public override string Key => "line";
    public override string Title => "线体管理";
}

public sealed class CraftworkViewModel : PageViewModelBase
{
    public override string Key => "craft";
    public override string Title => "工序管理";
}

public sealed class EquipmentViewModel : PageViewModelBase
{
    public override string Key => "eq";
    public override string Title => "机台管理";
}

public sealed class PlcViewModel : PageViewModelBase
{
    public override string Key => "plc";
    public override string Title => "PLC 管理";
    public override string Description => "核心控制枢纽，Phase 2 优先开发：连接管理、在线/离线检测、读写界面、点位映射、通信日志与告警。";
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

public sealed class FrameViewModel : PageViewModelBase
{
    public override string Key => "frame";
    public override string Title => "料架管理";
}
