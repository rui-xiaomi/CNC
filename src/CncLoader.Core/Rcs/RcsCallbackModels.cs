namespace CncLoader.Core.Rcs;

/// <summary>
/// RCS 三个入站回调（本系统作为 HTTP 服务端接收）：
/// pushTaskStatus（搬运/抓取结果）、scanTaskStatus（识别结果）、warnCallback（严重告警，10s/次）。
/// 契约见 <c>docs/agv对外接口.docx</c> §3.5/3.6/3.7 与开发文档 §12。
/// 回调只做"落库 + 发内部事件 + 应答 {taskId}"，长逻辑（跟踪/复核/账目）由后续步骤订阅事件处理。
/// </summary>
public static class RcsCallbackInterfaces
{
    public const string PushTaskStatus = "pushTaskStatus";
    public const string ScanTaskStatus = "scanTaskStatus";
    public const string WarnCallback = "warnCallback";

    public const string PushTaskStatusPath = "/externalApi/pushTaskStatus";
    public const string ScanTaskStatusPath = "/externalApi/scanTaskStatus";
    public const string WarnCallbackPath = "/externalApi/warnCallback";
}

/// <summary>pushTaskStatus 的 error_code 约定：0=成功 / 1=错误 / 9=取消。</summary>
public static class RcsErrorCode
{
    public const int Success = 0;
    public const int Error = 1;
    public const int Cancel = 9;

    /// <summary>error_code → 本系统任务态。</summary>
    public static string ToTaskState(int errorCode) => errorCode switch
    {
        Success => RcsTaskState.Completed,
        Cancel => RcsTaskState.Canceled,
        _ => RcsTaskState.Failed
    };
}

/// <summary>任务结果回调事件（pushTaskStatus 解析后派发，供跟踪器/UI 订阅）。</summary>
public sealed record RcsTaskStatusEvent(string TaskId, int ErrorCode, string? Message, string TaskState)
{
    /// <summary>来源：callback（pushTaskStatus 回调）/ poll（queryTask 兜底轮询）/ autoRedo（跟踪器自动重做后再分发）。默认 callback。</summary>
    public string Source { get; init; } = "callback";
}

/// <summary>识别结果回调事件（scanTaskStatus 解析后派发；products 按下发孔位顺序）。</summary>
public sealed record RcsScanResultEvent(
    string TaskId, int ErrorCode, string? Code, IReadOnlyList<string> Products, string? Message);

/// <summary>严重告警回调事件（warnCallback data 数组逐项派发）。</summary>
public sealed record RcsWarnEvent(string RobotCode, string BeginTime, string WarnContent, string? TaskCode);
