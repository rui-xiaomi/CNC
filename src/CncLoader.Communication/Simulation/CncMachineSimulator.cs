using System.Collections.Concurrent;
using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Core.Signals;
using CncLoader.Core.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Communication.Simulation;

/// <summary>
/// CNC 机台行为模拟器（第四阶段⑤演示用）：在 PLC 模拟器（Modbus / FINS）寄存器之上叠加"加工位节拍"语义，
/// 使 PLC sim + RcsSimulator 能联跑完整上下料节拍。无真机时启用；现场关闭。
/// 写入向所有已注册模拟器广播（<see cref="ISimulatorRegisterStore"/>），只对登记了该 PLC 的模拟器生效，
/// 故不论 DB 里 PLC 配的是 ModbusTCP 还是 FINS 都能驱动。
/// 职责：
/// 1) RCS 上料任务 COMPLETED → 置该加工位 HasMat=ON、AllowLoad=OFF（模拟工件到位）；
/// 2) 调度器写 POS_TEST_START=1（经 IPlcWriteHook 回调）→ 延时后置 PosOk=ON（模拟检测完成）；
/// 3) RCS 下料任务 COMPLETED → 置 HasMat=OFF、AllowLoad=ON、Ok/Ng=OFF（模拟工件被取走）；
/// 4) 调度器写 POS_TEST_START=2 → 复位启动信号为 0、清内部 Processing 标记。
/// </summary>
public sealed class CncMachineSimulator : IHostedService, IAsyncDisposable, IPlcWriteHook
{
    private readonly ISimulatorRegisterStore[] _sims;
    private readonly IPlcPointSource _points;
    private readonly RcsCallbackNotifier _notifier;
    private readonly IRcsTaskStore _taskStore;
    private readonly ILocationMapService _locationMap;
    private readonly bool _useSimulator;
    private readonly double _ngRate;
    private readonly ILogger<CncMachineSimulator> _logger;
    private readonly ConcurrentDictionary<(long Plc, long Pos), PositionBehavior> _behaviors = new();
    private readonly ConcurrentDictionary<(long Eq, long Pos), PositionBehavior> _byEquipment = new();
    private readonly ConcurrentDictionary<long, MachineBehavior> _machines = new();
    private readonly ConcurrentDictionary<string, RcsTaskRow> _taskCache = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public CncMachineSimulator(IEnumerable<ISimulatorRegisterStore> simulators, IPlcPointSource points,
        RcsCallbackNotifier notifier, IRcsTaskStore taskStore, ILocationMapService locationMap,
        IOptions<AppOptions> options, ILogger<CncMachineSimulator> logger)
    {
        _sims = simulators.ToArray();
        _points = points;
        _notifier = notifier;
        _taskStore = taskStore;
        _locationMap = locationMap;
        _useSimulator = options.Value.Plc.UseSimulator;
        _ngRate = Math.Clamp(options.Value.Rcs.SimulatorNgRate, 0.0, 1.0);
        SkipMaterialArrival = options.Value.Rcs.SimulatorSkipMaterialArrival;
        _logger = logger;
    }

    /// <summary>演示注入：true=上料完成时不置 HasMat=ON（模拟 RCS 报完成但工件未到位），用于验证 PLC 复核收口告警。可由 appsettings <c>Rcs.SimulatorSkipMaterialArrival</c> 启动时注入。</summary>
    public bool SkipMaterialArrival { get; set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_useSimulator)
        {
            _logger.LogInformation("CNC 机台行为模拟器未启用（Plc.UseSimulator=false，现场真机模式）。");
            return;
        }
        try
        {
            var all = await _points.GetAllAsync(cancellationToken);
            foreach (var p in all)
            {
                var offset = RegisterAddress.ToRegisterIndex(p.RegisterAddress);
                // 机台级信号（门/安全，PositionId=null）：测试期模拟机台上报「安全、门关」，
                // 让保留了「机台安全」点位的机台（如 A基准）能正常上线。
                if (p.PositionId is null)
                {
                    if (p.Signal == SignalKey.MachineSafe || p.Signal == SignalKey.Door)
                    {
                        var mb = _machines.GetOrAdd(p.PlcId, k => new MachineBehavior { PlcId = k });
                        if (p.Signal == SignalKey.MachineSafe) { mb.SafeOffset = offset; mb.SafeOn = p.OnValue; }
                        else { mb.DoorOffset = offset; mb.DoorClosed = p.OffValue; }
                    }
                    continue;
                }
                var key = (p.PlcId, p.PositionId.Value);
                var b = _behaviors.GetOrAdd(key, k => new PositionBehavior { PlcId = k.Plc, PositionId = k.Pos });
                b.EquipmentId = p.EquipmentId;
                _byEquipment[(p.EquipmentId, p.PositionId.Value)] = b;
                switch (p.Signal)
                {
                    case SignalKey.PosTestStart: b.TestStartOffset = offset; b.TestStartOn = p.OnValue; b.TestStartOff = p.OffValue; break;
                    case SignalKey.PosHasMat: b.HasMatOffset = offset; b.HasMatOn = p.OnValue; b.HasMatOff = p.OffValue; break;
                    case SignalKey.PosAllowLoad: b.AllowLoadOffset = offset; b.AllowLoadOn = p.OnValue; b.AllowLoadOff = p.OffValue; break;
                    case SignalKey.PosOk: b.OkOffset = offset; b.OkOn = p.OnValue; b.OkOff = p.OffValue; break;
                    case SignalKey.PosNg: b.NgOffset = offset; b.NgOn = p.OnValue; b.NgOff = p.OffValue; break;
                }
            }

            // 初始：每个加工位 AllowLoad=ON、HasMat=OFF、Ok/Ng=OFF、TestStart=0
            foreach (var b in _behaviors.Values)
            {
                WriteRegister(b.PlcId, b.AllowLoadOffset, (ushort)b.AllowLoadOn);
                WriteRegister(b.PlcId, b.HasMatOffset, (ushort)b.HasMatOff);
                WriteRegister(b.PlcId, b.OkOffset, (ushort)b.OkOff);
                WriteRegister(b.PlcId, b.NgOffset, (ushort)b.NgOff);
                WriteRegister(b.PlcId, b.TestStartOffset, 0);
            }
            // 初始：机台安全=ON、门=关（测试期让保留机台安全点位的机台正常上线）
            foreach (var mb in _machines.Values)
                WriteMachineSafety(mb);
            _logger.LogInformation("CNC 机台行为模拟器装载 {N} 个加工位、{M} 台机台级信号", _behaviors.Count, _machines.Count);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "CNC 机台行为模拟器装载失败"); }

        _notifier.TaskStatusReceived += OnTaskStatusReceived;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _notifier.TaskStatusReceived -= OnTaskStatusReceived;
        _cts?.Cancel();
        if (_loopTask is not null) { try { await _loopTask; } catch { /* ignore */ } }
    }

    private void OnTaskStatusReceived(object? sender, RcsTaskStatusEvent e)
    {
        if (e.TaskState != RcsTaskState.Completed) return;
        _ = HandleCompletedAsync(e.TaskId);
    }

    private async Task HandleCompletedAsync(string taskId)
    {
        try
        {
            var row = await _taskStore.GetByTaskIdAsync(taskId);
            if (row is null || row.EquipmentId is null || row.PositionId is null) return;
            // 找该机台的 PLC（行为按 PlcId+PositionId 索引）
            var b = FindBehavior(row.EquipmentId.Value, row.PositionId.Value);
            if (b is null) return;
            if (row.TaskType == "1") // 下料完成 → 工件被取走
            {
                b.HasMaterial = false;
                WriteRegister(b.PlcId, b.HasMatOffset, (ushort)b.HasMatOff);
                WriteRegister(b.PlcId, b.AllowLoadOffset, (ushort)b.AllowLoadOn);
                WriteRegister(b.PlcId, b.OkOffset, (ushort)b.OkOff);
                WriteRegister(b.PlcId, b.NgOffset, (ushort)b.NgOff);
                _logger.LogInformation("CNC-sim EQ{Eq} POS{Pos} 下料完成 → HasMat=OFF/AllowLoad=ON", row.EquipmentId, row.PositionId);
                // 工序间直接交接：搬运用 cell；抓取 ToCode 是机台站码，须用 dstPos 落到目标工位
                await PlaceHandoffAsync(row);
            }
            else // 上料完成 → 工件到位
            {
                if (SkipMaterialArrival)
                {
                    _logger.LogWarning("CNC-sim EQ{Eq} POS{Pos} 上料完成但 SkipMaterialArrival=true，不置 HasMat（模拟复核不过）", row.EquipmentId, row.PositionId);
                }
                else
                {
                    b.HasMaterial = true;
                    WriteRegister(b.PlcId, b.HasMatOffset, (ushort)b.HasMatOn);
                    WriteRegister(b.PlcId, b.AllowLoadOffset, (ushort)b.AllowLoadOff);
                    _logger.LogInformation("CNC-sim EQ{Eq} POS{Pos} 上料完成 → HasMat=ON", row.EquipmentId, row.PositionId);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "CNC-sim 处理任务完成 {TaskId} 异常", taskId); }
    }

    private PositionBehavior? FindBehavior(long equipmentId, long positionId)
        => _byEquipment.TryGetValue((equipmentId, positionId), out var b) ? b : null;

    /// <summary>工序间直接交接：下料终点落到目标工位（HasMat=ON / AllowLoad=OFF）。</summary>
    private async Task PlaceHandoffAsync(RcsTaskRow row)
    {
        var toCode = row.ToCode;
        try
        {
            var byCode = await _locationMap.ResolveByRcsCodeAsync(toCode);
            var dest = byCode is { EquipmentId: not null, PositionId: not null }
                ? byCode
                : GrabHandoffDest.Resolve(
                    await _locationMap.GetAllAsync(),
                    toCode,
                    byCode,
                    GrabHandoffDest.TryReadDstPos(row.ReqParam));
            if (dest?.EquipmentId is not long dstEq || dest.PositionId is not long dstPos)
            {
                _logger.LogWarning("CNC-sim 工序间交接未落到工位 toCode={Code}（抓取站码需 dstPos 或工位 cell）", toCode);
                return;
            }
            var target = FindBehavior(dstEq, dstPos);
            if (target is null) return;
            target.HasMaterial = true;
            WriteRegister(target.PlcId, target.HasMatOffset, (ushort)target.HasMatOn);
            WriteRegister(target.PlcId, target.AllowLoadOffset, (ushort)target.AllowLoadOff);
            _logger.LogInformation("CNC-sim 工序间交接件落到 EQ{Eq} POS{Pos}（终点 {Cell}）→ HasMat=ON",
                dstEq, dstPos, dest.RcsCode);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "CNC-sim 处理工序间交接落位异常 toCode={Cell}", toCode); }
    }

    /// <summary>向所有模拟器广播写入；只对登记了该 PLC 的模拟器生效（不论 Modbus/FINS）。</summary>
    private void WriteRegister(long plcId, int offset, ushort value)
    {
        foreach (var sim in _sims) sim.WriteRegister(plcId, offset, value);
    }

    /// <summary>置该机台「安全=ON、门=关」（幂等，测试期让机台正常上线）。</summary>
    private void WriteMachineSafety(MachineBehavior mb)
    {
        if (mb.SafeOffset != 0) WriteRegister(mb.PlcId, mb.SafeOffset, (ushort)mb.SafeOn);
        if (mb.DoorOffset != 0) WriteRegister(mb.PlcId, mb.DoorOffset, (ushort)mb.DoorClosed);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // 机台安全/门每拍幂等重置（同 AllowLoad，避免初始写入早于 PLC 登记而丢失）
                foreach (var mb in _machines.Values)
                    WriteMachineSafety(mb);
                foreach (var b in _behaviors.Values)
                    DrivePosition(b);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "CNC-sim 循环异常"); }
            try { await Task.Delay(200, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>IPlcWriteHook：调度器写 POS_TEST_START 成功后回调。value=1 启动检测 / 2 复位。</summary>
    public void OnTestStartWritten(long equipmentId, long positionId, int value)
    {
        if (!_byEquipment.TryGetValue((equipmentId, positionId), out var b)) return;
        if (value == b.TestStartOn)
        {
            b.Processing = true;
            b.ProcessStart = DateTime.UtcNow;
            _logger.LogInformation("CNC-sim EQ{Eq} POS{Pos} 收到启动信号，开始模拟检测", equipmentId, positionId);
        }
        else if (value == b.TestStartOff)
        {
            WriteRegister(b.PlcId, b.TestStartOffset, 0);
            b.Processing = false;
        }
    }

    private void DrivePosition(PositionBehavior b)
    {
        // 空闲加工位（无料且未在检测）持续保持 AllowLoad=ON。幂等，且不受"初始写入早于 PLC 登记"的启动时序影响
        // （PlcRuntimeBootstrapper.AddPlc 在窗口显示后才执行，早于此的初始写入会 no-op 丢失）。
        // 有料/检测中不触碰 AllowLoad，交由 RCS 完成回调驱动。
        if (!b.HasMaterial && !b.Processing && b.AllowLoadOffset != 0)
            WriteRegister(b.PlcId, b.AllowLoadOffset, (ushort)b.AllowLoadOn);

        if (b.TestStartOffset == 0) return;
        if (b.Processing && (DateTime.UtcNow - b.ProcessStart).TotalMilliseconds >= 2000)
        {
            b.Processing = false;
            if (_ngRate > 0 && Random.Shared.NextDouble() < _ngRate)
            {
                WriteRegister(b.PlcId, b.NgOffset, (ushort)b.NgOn);
                _logger.LogInformation("CNC-sim POS{Pos} 检测完成 → PosNg=ON（NG 分流）", b.PositionId);
            }
            else
            {
                WriteRegister(b.PlcId, b.OkOffset, (ushort)b.OkOn);
                _logger.LogInformation("CNC-sim POS{Pos} 检测完成 → PosOk=ON", b.PositionId);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class PositionBehavior
    {
        public long PlcId { get; init; }
        public long PositionId { get; init; }
        public long EquipmentId;
        public int TestStartOffset; public int TestStartOn = 1; public int TestStartOff = 2;
        public int HasMatOffset; public int HasMatOn = 1; public int HasMatOff = 2;
        public int AllowLoadOffset; public int AllowLoadOn = 1; public int AllowLoadOff = 2;
        public int OkOffset; public int OkOn = 1; public int OkOff = 2;
        public int NgOffset; public int NgOn = 1; public int NgOff = 2;
        public bool Processing; public DateTime ProcessStart;
        public bool HasMaterial;
    }

    /// <summary>机台级信号（门/安全）模拟：置安全=ON、门=关，让机台正常上线。</summary>
    private sealed class MachineBehavior
    {
        public long PlcId;
        public int SafeOffset; public int SafeOn = 1;
        public int DoorOffset; public int DoorClosed = 2;
    }
}
