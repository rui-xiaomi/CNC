namespace CncLoader.Core.Rcs;

/// <summary>
/// RCS 回调处理器：接收原始报文体 → 落库(IN) + 幂等去重 + 任务态推进 / 告警落库 + 派发内部事件，
/// 返回应答报文体（JSON 字符串，形如 <c>{"taskId":"..."}</c>）。
/// 由 <c>RcsCallbackHost</c>（内嵌 Kestrel）在收到 POST 时调用；实现须吞掉自身异常并总能返回应答。
/// </summary>
public interface IRcsCallbackProcessor
{
    /// <summary>pushTaskStatus：搬运/抓取任务结果推送。</summary>
    Task<string> HandlePushTaskStatusAsync(string rawBody, CancellationToken ct = default);

    /// <summary>scanTaskStatus：识别/盘点结果推送。</summary>
    Task<string> HandleScanTaskStatusAsync(string rawBody, CancellationToken ct = default);

    /// <summary>warnCallback：严重告警推送（10s/次，data 数组）。</summary>
    Task<string> HandleWarnCallbackAsync(string rawBody, CancellationToken ct = default);

    /// <summary>
    /// redo / redispatch 成功后调用：清除该 taskId 的 push/scan 去重键，
    /// 允许同 taskId 再次 FAILED 进入自动 redo 链（仍保留同一次失败的重复推送去重）。
    /// </summary>
    void ForgetTask(string taskId);
}
