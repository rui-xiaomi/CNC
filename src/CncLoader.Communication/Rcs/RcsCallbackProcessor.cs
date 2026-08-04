using System.Text.Json;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.Rcs;

/// <summary>
/// RCS 回调处理器实现：解析原始报文 → 落库(IN 报文流水) + 幂等去重 + 任务态推进 / 告警落库 + 派发内部事件。
/// 只做短逻辑，长逻辑（跟踪/复核/账目）由订阅 <see cref="RcsCallbackNotifier"/> 事件的后续步骤处理。
/// 所有 Handle* 都吞掉自身异常并总能返回应答报文，避免让 RCS 侧收到 5xx 而反复重推。
/// 去重：in-flight + final seen；仅必要持久化明确成功后提交 final seen。
/// </summary>
public sealed class RcsCallbackProcessor : IRcsCallbackProcessor
{
    private static readonly JsonSerializerOptions AckJsonOpt = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IRcsMessageLog _msgLog;
    private readonly IRcsTaskStore _store;
    private readonly IAlarmEventService _alarms;
    private readonly RcsCallbackNotifier _notifier;
    private readonly ILogger<RcsCallbackProcessor> _logger;
    private readonly CallbackDeduplicationGate _dedupe = new();

    public RcsCallbackProcessor(
        IRcsMessageLog msgLog,
        IRcsTaskStore store,
        IAlarmEventService alarms,
        RcsCallbackNotifier notifier,
        ILogger<RcsCallbackProcessor> logger)
    {
        _msgLog = msgLog;
        _store = store;
        _alarms = alarms;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<string> HandlePushTaskStatusAsync(string rawBody, CancellationToken ct = default)
    {
        string? taskId = null;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            taskId = ReadString(root, "taskId");
            var (errorCode, msg) = ReadSystem(root);

            var ack = BuildAck(taskId);
            await LogInAsync(RcsCallbackInterfaces.PushTaskStatus, RcsCallbackInterfaces.PushTaskStatusPath,
                taskId, rawBody, ack, ct);

            if (string.IsNullOrWhiteSpace(taskId))
            {
                _logger.LogWarning("pushTaskStatus 缺少 taskId，忽略：{Body}", Truncate(rawBody));
                return ack;
            }

            var state = RcsErrorCode.ToTaskState(errorCode);
            var dedupKey = $"push:{taskId}:{errorCode}";
            var result = await _dedupe.ExecuteAsync(dedupKey, async token =>
                await _store.UpdateStateAsync(taskId, state,
                    rcsStatus: state.ToLowerInvariant(),
                    error: errorCode == RcsErrorCode.Success ? null : msg, token), ct);

            // final seen 仅在 UpdateStateAsync=true 后提交；false/异常不进 seen、不发成功事件。
            switch (result.Outcome)
            {
                case CallbackDedupOutcome.Persisted:
                    _notifier.RaiseTaskStatus(new RcsTaskStatusEvent(taskId, errorCode, msg, state));
                    _logger.LogInformation("pushTaskStatus 任务 {TaskId} error_code={Code} → {State}",
                        taskId, errorCode, state);
                    break;
                case CallbackDedupOutcome.Duplicate:
                    _logger.LogDebug("pushTaskStatus 重复推送忽略：{Key}", SanitizeKey(dedupKey));
                    break;
                case CallbackDedupOutcome.Failed:
                    _logger.LogWarning(
                        "pushTaskStatus 持久化失败：type=push key={Key} stage=UpdateStateAsync error={Error}",
                        SanitizeKey(dedupKey), result.ErrorMessage);
                    await TryLogInAsync(RcsCallbackInterfaces.PushTaskStatus, RcsCallbackInterfaces.PushTaskStatusPath,
                        taskId, rawBody, result.ErrorMessage ?? "UpdateStateAsync failed", ct);
                    break;
                case CallbackDedupOutcome.Cancelled:
                    _logger.LogDebug("pushTaskStatus 处理取消：key={Key}", SanitizeKey(dedupKey));
                    break;
            }

            return ack;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "pushTaskStatus 处理异常：{Body}", Truncate(rawBody));
            await TryLogInAsync(RcsCallbackInterfaces.PushTaskStatus, RcsCallbackInterfaces.PushTaskStatusPath,
                taskId, rawBody, ex.Message, ct);
            return BuildAck(taskId);
        }
    }

    public async Task<string> HandleScanTaskStatusAsync(string rawBody, CancellationToken ct = default)
    {
        string? taskId = null;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            taskId = ReadString(root, "taskId");
            var (errorCode, msg) = ReadSystem(root);
            var code = ReadDataString(root, "code");
            var products = ReadProducts(root);

            var ack = BuildAck(taskId);
            await LogInAsync(RcsCallbackInterfaces.ScanTaskStatus, RcsCallbackInterfaces.ScanTaskStatusPath,
                taskId, rawBody, ack, ct);

            if (string.IsNullOrWhiteSpace(taskId))
            {
                _logger.LogWarning("scanTaskStatus 缺少 taskId，忽略：{Body}", Truncate(rawBody));
                return ack;
            }

            var state = RcsErrorCode.ToTaskState(errorCode);
            var dedupKey = $"scan:{taskId}:{errorCode}";
            var result = await _dedupe.ExecuteAsync(dedupKey, async token =>
                await _store.UpdateStateAsync(taskId, state,
                    rcsStatus: state.ToLowerInvariant(),
                    error: errorCode == RcsErrorCode.Success ? null : msg, token), ct);

            switch (result.Outcome)
            {
                case CallbackDedupOutcome.Persisted:
                    // 盘点校正（按 code+顺序全量落账）在步骤⑥订阅本事件实现；本步骤只落库+派发。
                    _notifier.RaiseScanResult(new RcsScanResultEvent(taskId, errorCode, code, products, msg));
                    _logger.LogInformation("scanTaskStatus 任务 {TaskId} 料架 {Code} 扫得 {N} 个二维码 → {State}",
                        taskId, code, products.Count, state);
                    break;
                case CallbackDedupOutcome.Duplicate:
                    _logger.LogDebug("scanTaskStatus 重复推送忽略：{Key}", SanitizeKey(dedupKey));
                    break;
                case CallbackDedupOutcome.Failed:
                    _logger.LogWarning(
                        "scanTaskStatus 持久化失败：type=scan key={Key} stage=UpdateStateAsync error={Error}",
                        SanitizeKey(dedupKey), result.ErrorMessage);
                    await TryLogInAsync(RcsCallbackInterfaces.ScanTaskStatus, RcsCallbackInterfaces.ScanTaskStatusPath,
                        taskId, rawBody, result.ErrorMessage ?? "UpdateStateAsync failed", ct);
                    break;
                case CallbackDedupOutcome.Cancelled:
                    _logger.LogDebug("scanTaskStatus 处理取消：key={Key}", SanitizeKey(dedupKey));
                    break;
            }

            return ack;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "scanTaskStatus 处理异常：{Body}", Truncate(rawBody));
            await TryLogInAsync(RcsCallbackInterfaces.ScanTaskStatus, RcsCallbackInterfaces.ScanTaskStatusPath,
                taskId, rawBody, ex.Message, ct);
            return BuildAck(taskId);
        }
    }

    public async Task<string> HandleWarnCallbackAsync(string rawBody, CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            var root = doc.RootElement;

            // warnCallback 无 taskId，应答空 taskId 以保持应答格式一致。
            var ack = BuildAck(null);
            await LogInAsync(RcsCallbackInterfaces.WarnCallback, RcsCallbackInterfaces.WarnCallbackPath,
                null, rawBody, ack, ct);

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                _logger.LogWarning("warnCallback data 非数组，忽略：{Body}", Truncate(rawBody));
                return ack;
            }

            foreach (var item in data.EnumerateArray())
            {
                var robotCode = ReadString(item, "robotCode") ?? "";
                var beginTime = ReadString(item, "beginTime") ?? "";
                var warnContent = ReadString(item, "warnContent") ?? "";
                var taskCode = ReadString(item, "taskCode");

                // 去重键 = robotCode + beginTime + warnContent（同一告警 10s/次重推）。
                var dedupKey = $"warn:{robotCode}|{beginTime}|{warnContent}";
                var result = await _dedupe.ExecuteAsync(dedupKey, async token =>
                {
                    await _alarms.RaiseRcsWarnAsync(robotCode, beginTime, warnContent, taskCode, token);
                    return true;
                }, ct);

                switch (result.Outcome)
                {
                    case CallbackDedupOutcome.Persisted:
                        _notifier.RaiseWarn(new RcsWarnEvent(robotCode, beginTime, warnContent, taskCode));
                        _logger.LogWarning("warnCallback 严重告警 车{Robot} {Content}{Task}",
                            robotCode, warnContent, string.IsNullOrWhiteSpace(taskCode) ? "" : $"（任务 {taskCode}）");
                        break;
                    case CallbackDedupOutcome.Duplicate:
                        _logger.LogDebug("warnCallback 重复告警忽略：{Key}", SanitizeKey(dedupKey));
                        break;
                    case CallbackDedupOutcome.Failed:
                        _logger.LogWarning(
                            "warnCallback 持久化失败：type=warn key={Key} stage=RaiseRcsWarnAsync error={Error}",
                            SanitizeKey(dedupKey), result.ErrorMessage);
                        break;
                    case CallbackDedupOutcome.Cancelled:
                        _logger.LogDebug("warnCallback 处理取消：key={Key}", SanitizeKey(dedupKey));
                        break;
                }
            }

            return ack;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "warnCallback 处理异常：{Body}", Truncate(rawBody));
            await TryLogInAsync(RcsCallbackInterfaces.WarnCallback, RcsCallbackInterfaces.WarnCallbackPath,
                null, rawBody, ex.Message, ct);
            return BuildAck(null);
        }
    }

    private static string BuildAck(string? taskId)
        => JsonSerializer.Serialize(new { taskId = taskId ?? "" }, AckJsonOpt);

    public void ForgetTask(string taskId) => _dedupe.ForgetTask(taskId);

    private async Task LogInAsync(string iface, string path, string? taskId, string reqBody, string ackBody, CancellationToken ct)
    {
        try
        {
            await _msgLog.LogAsync(new RcsMsgEntry("IN", iface, path, taskId, reqBody, ackBody, null, true, null), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RCS 回调报文流水落库失败（不影响应答）");
        }
    }

    private async Task TryLogInAsync(string iface, string path, string? taskId, string reqBody, string error, CancellationToken ct)
    {
        try
        {
            await _msgLog.LogAsync(new RcsMsgEntry("IN", iface, path, taskId, reqBody, null, null, false, error), ct);
        }
        catch { /* 兜底日志失败不再抛出 */ }
    }

    private static (int errorCode, string? msg) ReadSystem(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return (RcsErrorCode.Error, null);
        if (!data.TryGetProperty("system", out var system) || system.ValueKind != JsonValueKind.Object)
            return (RcsErrorCode.Error, null);

        var code = ReadInt(system, "error_code") ?? RcsErrorCode.Error;
        var msg = ReadString(system, "msg");
        return (code, msg);
    }

    private static string? ReadDataString(JsonElement root, string name)
    {
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            return ReadString(data, name);
        return null;
    }

    private static IReadOnlyList<string> ReadProducts(JsonElement root)
    {
        var list = new List<string>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in products.EnumerateArray())
                list.Add(p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "") : p.ToString());
        }
        return list;
    }

    private static string? ReadString(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? ReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
            _ => null
        };
    }

    private static string Truncate(string? s)
        => s is null ? "" : (s.Length > 1000 ? s[..1000] : s);

    /// <summary>脱敏去重键：截断过长内容，避免 warnContent 等完整敏感字段刷屏。</summary>
    private static string SanitizeKey(string key)
        => key.Length <= 120 ? key : key[..120] + "…";
}
