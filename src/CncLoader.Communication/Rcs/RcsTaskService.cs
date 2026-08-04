using System.Text.Json;
using CncLoader.Core.Rcs;
using Microsoft.Extensions.Logging;

namespace CncLoader.Communication.Rcs;

/// <summary>
/// RCS 任务编排：生成 taskId → 先落库(CREATED) → 下发 → 成功 DISPATCHED / 失败 FAILED。
/// 报文流水由 <see cref="IRcsClient"/> 记录。
/// 新执行（搬运 / Redo / Redispatch）经受管路由解析 + 权威活动校验门禁。
/// </summary>
public sealed class RcsTaskService : IRcsTaskService
{
    public const string RouteUnavailableUiMessage = "路由配置已禁用或不可用";

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IRcsClient _client;
    private readonly IRcsTaskStore _store;
    private readonly IRcsMessageLog _msgLog;
    private readonly IRcsCallbackProcessor _callbackProcessor;
    private readonly IManagedDispatchRouteResolver _routeResolver;
    private readonly IRoutingAvailabilityValidator _routingValidator;
    private readonly ILogger<RcsTaskService> _logger;

    public RcsTaskService(
        IRcsClient client,
        IRcsTaskStore store,
        IRcsMessageLog msgLog,
        IRcsCallbackProcessor callbackProcessor,
        IManagedDispatchRouteResolver routeResolver,
        IRoutingAvailabilityValidator routingValidator,
        ILogger<RcsTaskService> logger)
    {
        _client = client;
        _store = store;
        _msgLog = msgLog;
        _callbackProcessor = callbackProcessor;
        _routeResolver = routeResolver;
        _routingValidator = routingValidator;
        _logger = logger;
    }

    public async Task<RcsResult> DispatchTransitAsync(TransitDispatchArgs args, CancellationToken ct = default)
    {
        // 发送边界 Final（权威重读）；手动路径 ViewModel 已做 Pre，此处不可省略
        if (!args.SkipManagedRouteGate)
        {
            var final = await ValidateFinalAsync(args.FromCode, args.ToCode, ct);
            if (!final.Ok) return final.Failure!;
        }

        var taskId = string.IsNullOrWhiteSpace(args.TaskId)
            ? RcsTaskId.Next(args.LineCode, args.Kind)
            : args.TaskId;
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
        {
            if (!await _store.UpdateStateAsync(rcsTaskId, RcsTaskState.Canceled, "canceled", null, ct))
                _logger.LogWarning("取消任务 {TaskId} RCS 已成功但本地态未更新（任务不存在）", rcsTaskId);
        }
        else
            _logger.LogWarning("取消任务 {TaskId} 失败：{Msg}", rcsTaskId, result.Message ?? result.Error);
        return result;
    }

    public async Task<RcsResult> RedoAsync(string rcsTaskId, CancellationToken ct = default)
    {
        var row = await _store.GetByTaskIdAsync(rcsTaskId, ct);
        if (row is null) return RcsResult.Fail("", $"任务不存在：{rcsTaskId}");

        var gate = await ResolveAndValidateForNewExecutionAsync(row.FromCode, row.ToCode, ct);
        if (!gate.Ok) return gate.Failure!;

        var final = await ValidateFinalAsync(row.FromCode, row.ToCode, ct);
        if (!final.Ok) return final.Failure!;

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

        var gate = await ResolveAndValidateForNewExecutionAsync(row.FromCode, row.ToCode, ct);
        if (!gate.Ok) return gate.Failure!;

        var final = await ValidateFinalAsync(row.FromCode, row.ToCode, ct);
        if (!final.Ok) return final.Failure!;

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
            Kind = RcsTaskKind.PalletReturn, Author = author,
            SkipManagedRouteGate = true
        }, ct);

    /// <summary>
    /// 新执行统一门禁：Resolve + ValidatePre。
    /// 不修改任务、不 Increment、不调用 RCS。
    /// </summary>
    private async Task<GateOutcome> ResolveAndValidateForNewExecutionAsync(
        string? fromCode, string? toCode, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            ManagedDispatchRouteResult resolved;
            try
            {
                resolved = await _routeResolver.ResolveAsync(fromCode, toCode, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "受管路由解析异常，fail-closed");
                return GateOutcome.Reject(RcsResult.ConfigurationUnavailable(RouteUnavailableUiMessage));
            }

            var ctx = resolved.IsResolved
                ? resolved.Context!
                : BuildFailClosedContext(fromCode, toCode);

            RoutingAvailabilityResult pre;
            try
            {
                pre = await _routingValidator.ValidateAsync(ctx, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "路由 Pre 校验异常，fail-closed");
                return GateOutcome.Reject(RcsResult.ConfigurationUnavailable(RouteUnavailableUiMessage));
            }

            if (!resolved.IsResolved || !pre.IsAvailable)
            {
                var kind = MapFailureKind(resolved, pre);
                return GateOutcome.Reject(kind == RcsFailureKind.ConfigurationUnavailable
                    ? RcsResult.ConfigurationUnavailable(RouteUnavailableUiMessage)
                    : RcsResult.RouteUnavailable(RouteUnavailableUiMessage));
            }

            return GateOutcome.Pass(ctx);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private async Task<GateOutcome> ValidateFinalAsync(
        string? fromCode, string? toCode, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            ManagedDispatchRouteResult resolved;
            try
            {
                resolved = await _routeResolver.ResolveAsync(fromCode, toCode, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "受管路由 Final 解析异常，fail-closed");
                return GateOutcome.Reject(RcsResult.ConfigurationUnavailable(RouteUnavailableUiMessage));
            }

            if (!resolved.IsResolved)
                return GateOutcome.Reject(RcsResult.RouteUnavailable(RouteUnavailableUiMessage));

            RoutingAvailabilityResult final;
            try
            {
                final = await _routingValidator.ValidateAsync(resolved.Context!, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "路由 Final 校验异常，fail-closed");
                return GateOutcome.Reject(RcsResult.ConfigurationUnavailable(RouteUnavailableUiMessage));
            }

            if (!final.IsAvailable)
            {
                var kind = final.Reason == RoutingUnavailableReason.ConfigurationUnavailable
                    ? RcsFailureKind.ConfigurationUnavailable
                    : RcsFailureKind.RouteUnavailable;
                return GateOutcome.Reject(kind == RcsFailureKind.ConfigurationUnavailable
                    ? RcsResult.ConfigurationUnavailable(RouteUnavailableUiMessage)
                    : RcsResult.RouteUnavailable(RouteUnavailableUiMessage));
            }

            return GateOutcome.Pass(resolved.Context!);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static DispatchRouteContext BuildFailClosedContext(string? fromCode, string? toCode) => new()
    {
        SourceEquipmentId = 0,
        DestEquipmentId = RouteDependency.RequiredMissing,
        FromCode = fromCode,
        ToCode = toCode,
        RequiresResolvedCells = true
    };

    private static RcsFailureKind MapFailureKind(
        ManagedDispatchRouteResult resolved, RoutingAvailabilityResult pre)
    {
        if (resolved.Status == ManagedDispatchRouteStatus.ConfigurationUnavailable
            || pre.Reason == RoutingUnavailableReason.ConfigurationUnavailable)
            return RcsFailureKind.ConfigurationUnavailable;
        return RcsFailureKind.RouteUnavailable;
    }

    private async Task FinishAsync(string taskId, RcsResult result, CancellationToken ct)
    {
        if (result.Success)
            await _store.SetDispatchedAsync(taskId, ct);
        else if (!await _store.UpdateStateAsync(taskId, RcsTaskState.Failed, null, result.Message ?? result.Error, ct))
            _logger.LogWarning("下发失败后落库 FAILED 未生效（任务不存在）：{TaskId}", taskId);
    }

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

    private sealed class GateOutcome
    {
        public bool Ok { get; private init; }
        public RcsResult? Failure { get; private init; }
        public DispatchRouteContext? Context { get; private init; }

        public static GateOutcome Pass(DispatchRouteContext ctx) => new()
        {
            Ok = true,
            Context = ctx
        };

        public static GateOutcome Reject(RcsResult failure) => new()
        {
            Ok = false,
            Failure = failure
        };
    }
}
