namespace CncLoader.Core.Rcs;

/// <summary>
/// 本机 RCS 回调监听状态（由 <c>RcsCallbackHost</c> 实现）。
/// 供 UI「测试回调」判断 Kestrel 是否已绑定，以及探测实际监听地址。
/// </summary>
public interface IRcsCallbackListener
{
    /// <summary>Kestrel 是否已成功启动并在监听。</summary>
    bool IsListening { get; }

    /// <summary>启动失败原因（端口占用等）；成功时为 null。</summary>
    string? ListenError { get; }

    /// <summary>实际绑定的 Host（启动快照）。</summary>
    string BoundHost { get; }

    /// <summary>实际绑定的 Port（启动快照）。</summary>
    int BoundPort { get; }
}
