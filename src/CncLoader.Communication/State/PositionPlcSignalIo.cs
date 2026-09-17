using System.Collections.Concurrent;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Signals;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.State;

/// <summary>POS_TEST_START 写与 HasMat 新鲜读。未知一律 null，不得当无料。</summary>
internal sealed class PositionPlcSignalIo
{
    private readonly ConcurrentDictionary<(long Eq, long Pos), (long PlcId, string RegAddr)> _testStart = new();
    private readonly ConcurrentDictionary<(long Eq, long Pos), (long PlcId, string RegAddr, int OnValue, int OffValue)> _hasMat = new();
    private readonly IPlcOperationService _plcOps;
    private readonly IPlcWriteHook? _writeHook;
    private readonly ILogger _logger;

    public PositionPlcSignalIo(IPlcOperationService plcOps, ILogger logger, IPlcWriteHook? writeHook = null)
    {
        _plcOps = plcOps;
        _logger = logger;
        _writeHook = writeHook;
    }

    public bool HasHasMatPoint((long Eq, long Pos) key) => _hasMat.ContainsKey(key);

    public void RememberTestStart((long Eq, long Pos) key, long plcId, string registerAddress)
        => _testStart[key] = (plcId, registerAddress);

    public void RememberHasMat((long Eq, long Pos) key, long plcId, string registerAddress, int onValue, int offValue)
        => _hasMat[key] = (plcId, registerAddress, onValue, offValue);

    public bool TryGetTestStartPlc((long Eq, long Pos) key, out long plcId)
    {
        if (_testStart.TryGetValue(key, out var tp))
        {
            plcId = tp.PlcId;
            return true;
        }
        plcId = 0;
        return false;
    }

    public async Task<bool> WriteTestStartAsync(PositionContext ctx, int value, CancellationToken ct)
    {
        if (!_testStart.TryGetValue((ctx.EquipmentId, ctx.PositionId), out var tp))
        {
            _logger.LogWarning("EQ{Eq} POS{Pos} 未配置 POS_TEST_START 写点位", ctx.EquipmentId, ctx.PositionId);
            return false;
        }

        var r = await _plcOps.WriteWithConfirmAsync(tp.PlcId, tp.RegAddr, value, "scheduler", ct);
        if (!r.Verified)
        {
            _logger.LogWarning("EQ{Eq} POS{Pos} 写 POS_TEST_START={V} 首次失败，250ms 后重试：{Err}",
                ctx.EquipmentId, ctx.PositionId, value, r.Error);
            try { await Task.Delay(250, ct); }
            catch (OperationCanceledException) { return false; }
            r = await _plcOps.WriteWithConfirmAsync(tp.PlcId, tp.RegAddr, value, "scheduler", ct);
            if (!r.Verified)
            {
                _logger.LogWarning("EQ{Eq} POS{Pos} 写 POS_TEST_START={V} 重试仍失败：{Err}",
                    ctx.EquipmentId, ctx.PositionId, value, r.Error);
                return false;
            }
            _logger.LogInformation("EQ{Eq} POS{Pos} 写 POS_TEST_START={V} 重试成功", ctx.EquipmentId, ctx.PositionId, value);
        }

        _writeHook?.OnTestStartWritten(ctx.EquipmentId, ctx.PositionId, value);
        return true;
    }

    public async Task<bool?> ReadHasMatFreshAsync(PositionContext ctx, CancellationToken ct)
    {
        if (!_hasMat.TryGetValue((ctx.EquipmentId, ctx.PositionId), out var hp))
            return null;
        try
        {
            var r = await _plcOps.ReadRegisterAsync(hp.PlcId, hp.RegAddr, 1, ct);
            if (r.Error is not null)
            {
                _logger.LogWarning("EQ{Eq} POS{Pos} 复核读 HasMat 失败：{Error}",
                    ctx.EquipmentId, ctx.PositionId, r.Error);
            }
            return HasMatReading.From(r.RawValue, hp.OnValue, hp.OffValue, r.Error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EQ{Eq} POS{Pos} 复核读 HasMat 失败", ctx.EquipmentId, ctx.PositionId);
            return null;
        }
    }

    public async Task<PositionState> RecheckHasMatAsync(PositionContext ctx, int failThreshold, CancellationToken ct)
    {
        var fresh = await ReadHasMatFreshAsync(ctx, ct);
        var phase = ctx.Phase!.Value;
        var recheck = ctx.HasMatRecheck.Evaluate(ctx.CurrentTaskId, phase, fresh, failThreshold);
        ctx.StatusDetail = recheck.StatusDetail;

        if (recheck.Decision == HasMatRecheckDecision.Hold)
        {
            _logger.LogWarning(
                "EQ{Eq} POS{Pos} 任务 {Task} HasMat fresh 读取未知，复核中（{Count}/{Threshold}），保持 TRANSPORTING",
                ctx.EquipmentId, ctx.PositionId, ctx.CurrentTaskId ?? "—", recheck.FailureCount, failThreshold);
        }
        else if (recheck.Decision == HasMatRecheckDecision.Alarm)
        {
            ctx.AlarmReason = PositionTransition.HasMatAlarmReason(fresh, phase, failThreshold);
        }

        return recheck.NextState;
    }
}
