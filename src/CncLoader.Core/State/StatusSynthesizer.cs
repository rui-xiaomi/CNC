namespace CncLoader.Core.State;

/// <summary>合成一个加工位状态所需的信号输入。布尔为 null 表示该信号未知/未读到。</summary>
public readonly record struct PositionSignalInput
{
    public bool PlcOnline { get; init; }
    public bool? MachineSafe { get; init; }
    public bool? DoorOpen { get; init; }
    public bool? HasMaterial { get; init; }
    public bool? AllowLoad { get; init; }
    public bool? Ok { get; init; }
    public bool? Ng { get; init; }
    /// <summary>是否已下发测试启动（来自操作流水/写记录）。用于区分"已上料"与"检测中"。</summary>
    public bool TestStarted { get; init; }
}

/// <summary>按文档 §3.4 由 PLC 信号合成加工位标准状态。</summary>
public interface IStatusSynthesizer
{
    PositionState Synthesize(in PositionSignalInput input);
}

public sealed class StatusSynthesizer : IStatusSynthesizer
{
    public PositionState Synthesize(in PositionSignalInput input)
    {
        // 1. 通信断 → 离线（最高优先，无法判定任何信号）
        if (!input.PlcOnline)
            return PositionState.Offline;

        // 2. 不安全/开门 → 报警（机台级，压制工位判定）
        if (input.MachineSafe == false || input.DoorOpen == true)
            return PositionState.Alarm;

        // 3. 完成判定优先于过程判定
        if (input.Ok == true) return PositionState.DoneOk;
        if (input.Ng == true) return PositionState.DoneNg;

        // 4. 有料：已启动→检测中；未启动→已上料
        if (input.HasMaterial == true)
            return input.TestStarted ? PositionState.Processing : PositionState.Loaded;

        // 5. 无料且允许上料 → 等待上料
        if (input.HasMaterial == false && input.AllowLoad == true)
            return PositionState.WaitLoad;

        // 6. 无料且下料完成（曾有料）→ 已下料；信息不足时回到等待上料
        return input.AllowLoad == false ? PositionState.Unloaded : PositionState.WaitLoad;
    }
}
