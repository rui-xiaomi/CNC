using CncLoader.Core.Rcs;
using CncLoader.Core.State;

namespace CncLoader.Communication.State;

/// <summary>工位定态、看板发布与取消占用提示。</summary>
internal sealed class PositionStatePublisher
{
    private readonly ISignalStateStore _store;
    private readonly IRcsTaskStore _taskStore;

    public PositionStatePublisher(ISignalStateStore store, IRcsTaskStore taskStore)
    {
        _store = store;
        _taskStore = taskStore;
    }

    public void SetState(PositionContext ctx, PositionState state)
    {
        var prev = ctx.State;
        if (prev != state)
            ctx.StateEnteredAt = DateTime.UtcNow;
        ctx.State = state;
        if (state != PositionState.Transporting)
        {
            ctx.HasMatRecheck.Reset();
            ctx.StatusDetail = null;
        }
        // Layer 1：进入 WAIT_LOAD 记空闲起点（供竞争排序）；离开 WAIT_LOAD 清"请求上料"标记与空闲计时。
        if (state == PositionState.WaitLoad)
        {
            if (prev != PositionState.WaitLoad) ctx.WaitLoadSince = DateTime.Now;
        }
        else
        {
            ctx.UploadRequested = false;
            ctx.WaitLoadSince = null;
        }
        PublishPosition(ctx);
    }

    public void PublishPosition(PositionContext ctx)
    {
        _store.UpdatePosition(new PositionStatus
        {
            EquipmentId = ctx.EquipmentId, PositionId = ctx.PositionId, State = ctx.State,
            MaterialId = ctx.MaterialId, StatusDetail = ctx.StatusDetail
        });
    }

    /// <summary>看板工位卡：未确认取消占用，须带任务号供 RCS 页确认。</summary>
    public async Task PublishCancelHoldAsync(PositionContext ctx, CancellationToken ct)
    {
        var wasHold = CancelHoldDisplay.IsHold(ctx.StatusDetail);
        if (ctx.State != PositionState.WaitLoad)
        {
            if (wasHold)
            {
                ctx.StatusDetail = null;
                PublishPosition(ctx);
            }
            return;
        }

        var ids = await _taskStore.ListUnconfirmedCanceledTaskIdsAsync(ctx.EquipmentId, ctx.PositionId, ct);
        var next = ids.Count > 0 ? CancelHoldDisplay.Format(ids) : null;
        if (ctx.StatusDetail == next) return;
        ctx.StatusDetail = next;
        PublishPosition(ctx);
    }
}
