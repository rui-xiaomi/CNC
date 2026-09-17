using CommunityToolkit.Mvvm.ComponentModel;
using CncLoader.Core.Abstractions;
using CncLoader.Core.State;

namespace CncLoader.UI.ViewModels.Pages;

/// <summary>启动对账状态条 + UI 异常计数。只展示，不驱动重试。</summary>
public sealed partial class DashboardReconcileBarViewModel : ObservableObject, IDisposable
{
    private readonly ReconciliationStatusBinder _binder;
    private readonly IUiExceptionMonitor? _uiExceptions;
    private bool _disposed;

    internal DashboardReconcileBarViewModel(
        IPositionScheduler scheduler,
        Action<Action> marshalToUi,
        IUiExceptionMonitor? uiExceptions)
    {
        _uiExceptions = uiExceptions;
        _binder = new ReconciliationStatusBinder(scheduler, marshalToUi);
        SyncFromBinder();
        _binder.Changed += OnBinderChanged;
        if (_uiExceptions is not null)
        {
            SyncUiExceptions();
            _uiExceptions.Changed += OnUiExceptionsChanged;
        }
    }

    [ObservableProperty] private string _reconcileTitle = "启动对账未开始";
    [ObservableProperty] private string _reconcileSubText = "自动派工尚未开启";
    [ObservableProperty] private string _reconcileBrushKey = "IdleBrush";
    [ObservableProperty] private string _reconcileSoftBrushKey = "SoftIdleBrush";
    [ObservableProperty] private string? _reconcileDetailToolTip;
    [ObservableProperty] private bool _isReconcileGateOpen;
    [ObservableProperty] private bool _hasUiExceptions;
    [ObservableProperty] private string _uiExceptionText = "";
    [ObservableProperty] private string? _uiExceptionToolTip;

    private void OnBinderChanged(object? sender, EventArgs e) => SyncFromBinder();

    private void SyncFromBinder()
    {
        ReconcileTitle = _binder.Title;
        ReconcileSubText = _binder.SubText;
        ReconcileBrushKey = _binder.BrushKey;
        ReconcileSoftBrushKey = _binder.SoftBrushKey;
        ReconcileDetailToolTip = _binder.DetailToolTip;
        IsReconcileGateOpen = _binder.IsGateOpen;
    }

    private void OnUiExceptionsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        SyncUiExceptions();
    }

    private void SyncUiExceptions()
    {
        if (_uiExceptions is null) return;
        var count = _uiExceptions.Count;
        HasUiExceptions = count > 0;
        UiExceptionText = count > 0 ? $"界面异常 {count}" : "";
        UiExceptionToolTip = count > 0
            ? $"UI 线程已拦截 {count} 次未处理异常，界面显示可能与实际不一致，请核对后择机重启客户端。最近一次 {_uiExceptions.LastAt:HH:mm:ss}：{_uiExceptions.LastMessage}"
            : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _binder.Changed -= OnBinderChanged;
        _binder.Dispose();
        if (_uiExceptions is not null) _uiExceptions.Changed -= OnUiExceptionsChanged;
    }
}
