namespace CncLoader.Core.State;

/// <summary>
/// 加工位标准状态，对应 MAS_AUTO_EQUIMENT_CONDITION 字典与文档 §3.4 / §7 状态机。
/// </summary>
public enum PositionState
{
    /// <summary>离线：PLC 连接断开/心跳失败。</summary>
    Offline,
    /// <summary>等待上料：允许上料=ON 且 有料=OFF。</summary>
    WaitLoad,
    /// <summary>已上料：有料=ON，未写测试启动。</summary>
    Loaded,
    /// <summary>检测中：已写测试启动且未出 OK/NG。</summary>
    Processing,
    /// <summary>检测 OK。</summary>
    DoneOk,
    /// <summary>检测 NG。</summary>
    DoneNg,
    /// <summary>已下料：有料=OFF（下料完成）。</summary>
    Unloaded,
    /// <summary>报警：机台安全=OFF 或 开门 或 通信异常。</summary>
    Alarm
}

public static class PositionStateNames
{
    public static string ToCode(PositionState s) => s switch
    {
        PositionState.Offline => "OFFLINE",
        PositionState.WaitLoad => "WAIT_LOAD",
        PositionState.Loaded => "LOADED",
        PositionState.Processing => "PROCESSING",
        PositionState.DoneOk => "DONE_OK",
        PositionState.DoneNg => "DONE_NG",
        PositionState.Unloaded => "UNLOADED",
        PositionState.Alarm => "ALARM",
        _ => "OFFLINE"
    };

    public static string ToDisplay(PositionState s) => s switch
    {
        PositionState.Offline => "离线",
        PositionState.WaitLoad => "等待上料",
        PositionState.Loaded => "已上料",
        PositionState.Processing => "检测中",
        PositionState.DoneOk => "检测OK",
        PositionState.DoneNg => "检测NG",
        PositionState.Unloaded => "已下料",
        PositionState.Alarm => "报警",
        _ => "离线"
    };
}
