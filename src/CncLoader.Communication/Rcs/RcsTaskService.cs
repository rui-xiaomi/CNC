using System.Text.Json;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.Rcs;

/// <summary>
/// RCS 任务编排：生成 taskId → 先落库(CREATED) → 下发 → 成功 DISPATCHED / 失败 FAILED。
/// 报文流水由 <see cref="IRcsClient"/> 记录。
/// </summary>
public sealed class RcsTaskService : IRcsTaskService
{
    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IRcsClient _client;
    private readonly IRcsTaskStore _store;
    private readonly IRcsMessageLog _msgLog;
    private readonly IRcsCallbackProcessor _callbackProcessor;
    private readonly ILogger<RcsTaskService> _logger;

    public RcsTaskService(IRcsClient client, IRcsTaskStore store, IRcsMessageLog msgLog,
        IRcsCallbackProcessor callbackProcessor, ILogger<RcsTaskService> logger)
    {
        _client = client;
        _store = store;
        _msgLog = msgLog;
        _callbackProcessor = callbackProcessor;
        _logger = logger;
    }

    public async Task<RcsResult> DispatchTransitAsync(TransitDispatchArgs args, CancellationToken ct = default)
    {
        var taskId = RcsTaskId.Next(args.LineCode, args.Kind);
        var reqParam = JsonSerializer.Serialize(new[]
        {
            new RcsPosition(args.FromCode, "cell"),
            new RcsPosition(args.ToCode, "cell")
        }, JsonOpt);

        await _store.CreateAsync(new RcsTaskRecord
        {
            RcsTaskId = taskId,
            Kind = args.Kind,
            WorkLineId = args.WorkLineId,
            TaskType = args.TaskType,
            Priority = args.Priority,
            FromCode = args.FromCode,
            ToCode = args.ToCode,
            EquipmentId = args.EquipmentId,
            PositionId = args.PositionId,
            CraftworkId = args.CraftworkId,
            MaterialId = args.MaterialId,
            TxnId = args.TxnId,
            ReqParam = reqParam,
            Author = args.Author
        }, ct);

        var req = new TransitTaskRequest
        {
            TaskId = taskId,
            TaskType = "move",
            Priority = args.Priority,
            Position =
            {
                new RcsPosition(args.FromCode, "cell"),
                new RcsPosition(args.ToCode, "cell")
            }
        };

        var result = await _client.TransitTaskAsync(req, ct);
        await FinishAsync(taskId, result, ct);
        return result with { TaskId = taskId };
    }

    public async Task<RcsResult> DispatchGrabAsync(GrabDispatchArgs args, CancellationToken ct = default)
    {
        var taskId = RcsTaskId.Next(args.LineCode, RcsTaskKind.Grab);
        var param = JsonSerializer.Serialize(args.Items, JsonOpt);

        await _store.CreateAsync(new RcsTaskRecord
        {
            RcsTaskId = taskId,
            Kind = RcsTaskKind.Grab,
            WorkLineId = args.WorkLineId,
            TaskType = "0",
            Priority = args.Priority,
            FromCode = args.SrcStation,
            ToCode = args.DstStation,
            EquipmentId = args.EquipmentId,
            PositionId = args.PositionId,
            MaterialId = args.MaterialId,
            ReqParam = param,
            Author = args.Author
        }, ct);

        var req = new ExcuteTaskRequest
        {
            TaskId = taskId,
            TaskType = "grabTask",
            Priority = args.Priority,
            Param = param,
            Position =
            {
                new RcsPosition(args.SrcStation, "station"),
                new RcsPosition(args.DstStation, "station")
            }
        };

        var result = await _client.ExcuteTaskAsync(req, ct);
        await FinishAsync(taskId, result, ct);
        return result with { TaskId = taskId };
    }

    public async Task<RcsResult> DispatchIdentifyAsync(IdentifyDispatchArgs args, CancellationToken ct = default)
    {
        var taskId = RcsTaskId.Next(args.LineCode, RcsTaskKind.Identify);
        var param = $"{args.PosStart},{args.Count}";

        await _store.CreateAsync(new RcsTaskRecord
        {
            RcsTaskId = taskId,
            Kind = RcsTaskKind.Identify,
            WorkLineId = args.WorkLineId,
            TaskType = "2",
            Priority = args.Priority,
            FromCode = args.Station,
            ToCode = args.Station,
            ReqParam = param,
            Author = args.Author
        }, ct);

        var req = new ExcuteTaskRequest
        {
            TaskId = taskId,
            TaskType = "identifyQR",
            Priority = args.Priority,
            Param = param,
            Position = { new RcsPosition(args.Station, "station") }
        };

        var result = await _client.ExcuteTaskAsync(req, ct);
        await FinishAsync(taskId, result, ct);
        return result with { TaskId = taskId };
    }

    public async Task<RcsResult> CancelAsync(string rcsTaskId, CancellationToken ct = default)
    {
        var req = new CancelTaskRequest { TaskId = rcsTaskId };
        var result = await _client.CancelTaskAsync(req, ct);
        if (result.Success)
            await _store.UpdateStateAsync(rcsTaskId, RcsTaskState.Canceled, "canceled", null, ct);
        else
            _logger.LogWarning("取消任务 {TaskId} 失败：{Msg}", rcsTaskId, result.Message ?? result.Error);
        return result;
    }

    public async Task<RcsResult> RedoAsync(string rcsTaskId, CancellationToken ct = default)
    {
        var row = await _store.GetByTaskIdAsync(rcsTaskId, ct);
        if (row is null) return RcsResult.Fail("", $"任务不存在：{rcsTaskId}");

        await _store.IncrementRedoAsync(rcsTaskId, ct);
        var result = await BuildAndSendAsync(row, "redo", ct);
        await FinishAsync(rcsTaskId, result, ct);
        if (result.Success) _callbackProcessor.ForgetTask(rcsTaskId);
        return result;
    }

    /// <summary>
    /// 自动重做专用：调用前应已通过 <see cref="IRcsTaskStore.TryIncrementRedoIfUnderAsync"/> 原子递增 REDO_COUNT，
    /// 此处只按落库参数重建请求并重发（同 taskId 幂等），不再递增计数。
    /// </summary>
    public async Task<RcsResult> RedispatchAsync(string rcsTaskId, CancellationToken ct = default)
    {
        var row = await _store.GetByTaskIdAsync(rcsTaskId, ct);
        if (row is null) return RcsResult.Fail("", $"任务不存在：{rcsTaskId}");

        var result = await BuildAndSendAsync(row, "redo", ct);
        await FinishAsync(rcsTaskId, result, ct);
        if (result.Success) _callbackProcessor.ForgetTask(rcsTaskId);
        return result;
    }

    public Task<RcsResult> QueryAsync(QueryTaskRequest req, CancellationToken ct = default)
        => _client.QueryTaskAsync(req, ct);

    public Task<IReadOnlyList<RcsTaskRow>> GetRecentTasksAsync(int limit = 100, CancellationToken ct = default)
        => _store.GetRecentAsync(limit, ct);

    public Task<IReadOnlyList<RcsMsgRow>> GetRecentMessagesAsync(int limit = 100, CancellationToken ct = default)
        => _msgLog.GetRecentAsync(limit, ct);

    public Task<IReadOnlyList<RcsMsgRow>> QueryMessagesAsync(RcsMsgQuery query, CancellationToken ct = default)
        => _msgLog.QueryAsync(query, ct);

    public Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default)
        => _store.ConfirmCancelHandledAsync(rcsTaskId, ct);

    public Task<RcsResult> DispatchPalletReturnAsync(long equipmentId, long? positionId, string fromCode, string toCode,
        long workLineId, string lineCode, string author, CancellationToken ct = default)
        => DispatchTransitAsync(new TransitDispatchArgs
        {
            WorkLineId = workLineId, LineCode = lineCode, TaskType = "2",
            Priority = 8, FromCode = fromCode, ToCode = toCode,
            EquipmentId = equipmentId, PositionId = positionId,
            Kind = RcsTaskKind.PalletReturn, Author = author
        }, ct);

    private async Task FinishAsync(string taskId, RcsResult result, CancellationToken ct)
    {
        if (result.Success)
            await _store.SetDispatchedAsync(taskId, ct);
        else
            await _store.UpdateStateAsync(taskId, RcsTaskState.Failed, null, result.Message ?? result.Error, ct);
    }

    /// <summary>按落库种类与参数重建请求并重发（同 taskId 幂等）。commandType 通常为 "redo"。</summary>
    private async Task<RcsResult> BuildAndSendAsync(RcsTaskRow row, string? commandType, CancellationToken ct)
    {
        var kind = row.Kind ?? "transit";
        if (kind is "grab")
        {
            var items = TryParse<List<GrabItem>>(row.ReqParam) ?? new();
            var req = new ExcuteTaskRequest
            {
                TaskId = row.RcsTaskId!,
                TaskType = "grabTask",
                Priority = row.Priority,
                CommandType = commandType,
                Param = JsonSerializer.Serialize(items, JsonOpt),
                Position = { new RcsPosition(row.FromCode, "station"), new RcsPosition(row.ToCode, "station") }
            };
            return await _client.ExcuteTaskAsync(req, ct);
        }
        if (kind is "identify")
        {
            var req = new ExcuteTaskRequest
            {
                TaskId = row.RcsTaskId!,
                TaskType = "identifyQR",
                Priority = row.Priority,
                CommandType = commandType,
                Param = row.ReqParam ?? "",
                Position = { new RcsPosition(row.FromCode, "station") }
            };
            return await _client.ExcuteTaskAsync(req, ct);
        }
        var tr = new TransitTaskRequest
        {
            TaskId = row.RcsTaskId!,
            TaskType = "move",
            Priority = row.Priority,
            CommandType = commandType,
            Position = { new RcsPosition(row.FromCode, "cell"), new RcsPosition(row.ToCode, "cell") }
        };
        return await _client.TransitTaskAsync(tr, ct);
    }

    private static T? TryParse<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, JsonOpt); }
        catch { return default; }
    }
}
