namespace CncLoader.Core.State;

/// <summary>
/// 看板对账状态绑定器：读取快照、订阅变化、映射文案。无 WPF 依赖；UI 线程切换由 marshal 回调完成。
/// </summary>
public sealed class ReconciliationStatusBinder : IDisposable
{
    private readonly IPositionScheduler _scheduler;
    private readonly Action<Action> _marshal;
    private bool _disposed;

    public ReconciliationStatusBinder(IPositionScheduler scheduler, Action<Action>? marshalToUi = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _marshal = marshalToUi ?? (a => a());
        Apply(ReadSnapshot());
        _scheduler.ReconciliationStateChanged += OnSchedulerChanged;
    }

    public string Title { get; private set; } = "";
    public string SubText { get; private set; } = "";
    public string BrushKey { get; private set; } = "IdleBrush";
    public string SoftBrushKey { get; private set; } = "SoftIdleBrush";
    public string? DetailToolTip { get; private set; }
    public bool IsGateOpen { get; private set; }

    /// <summary>绑定属性已更新（已在 marshal 回调内）。</summary>
    public event EventHandler? Changed;

    private ReconciliationSnapshot ReadSnapshot()
        => new(_scheduler.ReconciliationState, _scheduler.ReconciliationFailureReason, _scheduler.IsReconciled);

    private void OnSchedulerChanged(object? sender, ReconciliationSnapshot e)
    {
        if (_disposed) return;
        try
        {
            _marshal(() =>
            {
                if (_disposed) return;
                Apply(e);
                Changed?.Invoke(this, EventArgs.Empty);
            });
        }
        catch
        {
            // marshal / 订阅方异常不得回灌调度器
        }
    }

    private void Apply(ReconciliationSnapshot snap)
    {
        var mapped = ReconciliationStatusPresentation.Map(snap);
        Title = mapped.Title;
        SubText = mapped.SubText;
        BrushKey = mapped.BrushKey;
        SoftBrushKey = mapped.SoftBrushKey;
        DetailToolTip = mapped.DetailToolTip;
        IsGateOpen = mapped.IsGateOpen;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scheduler.ReconciliationStateChanged -= OnSchedulerChanged;
    }
}
