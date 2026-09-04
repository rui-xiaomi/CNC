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
    private readonly ISlotAccountService _slots;
    private readonly ILogger<RcsTaskService> _logger;

    /// <summary>
    /// 刻意 internal：入参含 internal 的 <see cref="IRcsClient"/>，
    /// 使「在容器外自己 new 一个绕过门禁的任务服务」在通信层之外无法编译。
    /// </summary>
    internal RcsTaskService(
        IRcsClient client,
        IRcsTaskStore store,
        IRcsMessageLog msgLog,
        IRcsCallbackProcessor callbackProcessor,
        IManagedDispatchRouteResolver routeResolver,
        IRoutingAvailabilityValidator routingValidator,
        ILogger<RcsTaskService> logger,
        ISlotAccountService slots)
    {
        _client = client;
        _store = store;
        _msgLog = msgLog;
        _callbackProcessor = callbackProcessor;
        _routeResolver = routeResolver;
        _routingValidator = routingValidator;
        _logger = logger;
        _slots = slots;
    }

    public async Task<RcsResult> DispatchTransitAsync(TransitDispatchArgs args, CancellationToken ct = default)
    {
        // 发送边界一次权威读取：Resolve→Validate→角色→Create→RCS
        // 无 Skip 逃生；PalletReturn / ChangeFrame 另在 Service 边界执行角色策略
        if (args.Operation == DispatchOperationKind.ChangeFrame
            && (args.EquipmentId is null or <= 0))
        {
            _logger.LogWarning("ChangeFrame 缺少机台上下文，拒绝派工 From={From} To={To}",
                args.FromCode, args.ToCode);
            return RcsResult.RouteUnavailable(RouteUnavailableUiMessage);
        }

        var gate = await ValidateFinalAsync(args, ct);
        if (!gate.Ok) return gate.Failure!;
        if (!MatchesOperationRole(args.Operation, gate.Context!))
            return RcsResult.RouteUnavailable(RouteUnavailableUiMessage);

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
        // Final-only：ResolveCurrent→ValidateFinal→Grab 角色→Create→Excute（方法本身固定操作语义）
        var final = await ValidateFinalAsync(args.SrcStation, args.DstStation, ct);
        if (!final.Ok) return final.Failure!;
        if (!MatchesGrabRole(final.Context!))
            return RcsResult.RouteUnavailable(RouteUnavailableUiMessage);

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
        // Final-only：单端点以 Station 作 From/To 解析；Identify 角色固定（不可由调用方 bool 绕过）
        var final = await ValidateFinalAsync(args.Station, args.Station, ct);
        if (!final.Ok) return final.Failure!;
        if (!MatchesIdentifyRole(final.Context!))
            return RcsResult.RouteUnavailable(RouteUnavailableUiMessage);

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

        var gate = await ValidateFinalAsync(row.FromCode, row.ToCode, ct);
        if (!gate.Ok) return gate.Failure!;

        var hold = await EnsureReplayReservationAsync(row, gate.Context!, ct);
        if (!hold.Ok) return hold.Failure!;

        await _store.IncrementRedoAsync(rcsTaskId, ct);
        var result = await BuildAndSendAsync(row, "redo", ct);
        if (!result.Success)
            await RollbackReplayIfCreatedAsync(hold, rcsTaskId, ct);
        await FinishAsync(rcsTaskId, result, ct);
        if (result.Success) _callbackProcessor.ForgetTask(rcsTaskId);
        return result;
    }

    /// <summary>
    /// 按落库参数重发（门禁后发送），不增加 REDO_COUNT。
    /// 与 <see cref="AutoRedispatchAsync"/>（门禁后原子 Claim）及手动 <see cref="RedoAsync"/> 分离。
    /// </summary>
    public async Task<RcsResult> RedispatchAsync(string rcsTaskId, CancellationToken ct = default)
    {
        var row = await _store.GetByTaskIdAsync(rcsTaskId, ct);
        if (row is null) return RcsResult.Fail("", $"任务不存在：{rcsTaskId}");

        var gate = await ValidateFinalAsync(row.FromCode, row.ToCode, ct);
        if (!gate.Ok) return gate.Failure!;

        var hold = await EnsureReplayReservationAsync(row, gate.Context!, ct);
        if (!hold.Ok) return hold.Failure!;

        var result = await BuildAndSendAsync(row, "redo", ct);
        if (!result.Success)
            await RollbackReplayIfCreatedAsync(hold, rcsTaskId, ct);
        await FinishAsync(rcsTaskId, result, ct);
        if (result.Success) _callbackProcessor.ForgetTask(rcsTaskId);
        return result;
    }

    /// <summary>
    /// Tracker 自动重派：Load→发送边界一次 Resolve+Validate→
    /// TryClaimAutoRedo→RcsSend。门禁失败不 Claim；Claim 失败不 Send；不回滚已消费次数。
    /// </summary>
    public async Task<RcsResult> AutoRedispatchAsync(string rcsTaskId, int maxRedoCount, CancellationToken ct = default)
    {
        var row = await _store.GetByTaskIdAsync(rcsTaskId, ct);
        if (row is null) return RcsResult.Fail("", $"任务不存在：{rcsTaskId}");

        var gate = await ValidateFinalAsync(row.FromCode, row.ToCode, ct);
        if (!gate.Ok) return gate.Failure!;

        var hold = await EnsureReplayReservationAsync(row, gate.Context!, ct);
        if (!hold.Ok) return hold.Failure!;

        var claim = await _store.TryClaimAutoRedoAsync(rcsTaskId, maxRedoCount, ct);
        if (claim != AutoRedoClaimResult.Claimed)
        {
            await RollbackReplayIfCreatedAsync(hold, rcsTaskId, ct);
            _logger.LogWarning(
                "自动重派 Claim 未成功 {TaskId} 结果={Claim}（非路由配置失败）",
                rcsTaskId, claim);
            return claim switch
            {
                AutoRedoClaimResult.LimitReached =>
                    RcsResult.RedoLimitReached($"自动重做已达上限 {maxRedoCount}"),
                AutoRedoClaimResult.NotFound =>
                    RcsResult.Fail("", $"任务不存在：{rcsTaskId}"),
                _ => RcsResult.AutoRedoNotClaimable($"自动重派 Claim 未抢占：{claim}")
            };
        }

        // Claim 已消费次数；取消则不发送、不回滚（D14 边界）。
        try
        {
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            await RollbackReplayIfCreatedAsync(hold, rcsTaskId, CancellationToken.None);
            _logger.LogWarning(
                "自动重派 Claim 成功后取消，已消耗 RedoCount，不发送 {TaskId}", rcsTaskId);
            throw;
        }

        var result = await BuildAndSendAsync(row, "redo", ct);
        if (!result.Success)
            await RollbackReplayIfCreatedAsync(hold, rcsTaskId, ct);
        await FinishAsync(rcsTaskId, result, ct);
        if (result.Success) _callbackProcessor.ForgetTask(rcsTaskId);
        return result;
    }

    public Task<RcsResult> QueryAsync(QueryTaskRequest req, CancellationToken ct = default)
        => _client.QueryTaskAsync(req, ct);

    public Task<RcsResult> ProbeQueryAsync(RcsConnectionConfig probe, QueryTaskRequest req, CancellationToken ct = default)
        => _client.QueryTaskAtAsync(req, probe, ct);

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
            Kind = RcsTaskKind.PalletReturn,
            Operation = DispatchOperationKind.PalletReturn,
            Author = author
        }, ct);

    /// <summary>Grab/Identify/Redo 发送边界（无换架 Operation 上下文）。</summary>
    private Task<GateOutcome> ValidateFinalAsync(
        string? fromCode, string? toCode, CancellationToken ct)
        => ResolveAndValidateCoreAsync(fromCode, toCode, operationArgs: null, ct);

    /// <summary>发送边界一次 Resolve+Validate。不修改任务、不 Increment、不调用 RCS。</summary>
    private Task<GateOutcome> ValidateFinalAsync(
        TransitDispatchArgs args, CancellationToken ct)
        => ResolveAndValidateCoreAsync(args.FromCode, args.ToCode, args, ct);

    private async Task<GateOutcome> ResolveAndValidateCoreAsync(
        string? fromCode, string? toCode, TransitDispatchArgs? operationArgs,
        CancellationToken ct)
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

            if (!resolved.IsResolved)
                return GateOutcome.Reject(RcsResult.RouteUnavailable(RouteUnavailableUiMessage));

            var ctx = ApplyOperationContext(resolved.Context!, operationArgs);

            RoutingAvailabilityResult gate;
            try
            {
                gate = await _routingValidator.ValidateAsync(ctx, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "路由校验异常，fail-closed");
                return GateOutcome.Reject(RcsResult.ConfigurationUnavailable(RouteUnavailableUiMessage));
            }

            if (!gate.IsAvailable)
            {
                return GateOutcome.Reject(gate.Reason == RoutingUnavailableReason.ConfigurationUnavailable
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

    private static DispatchRouteContext ApplyOperationContext(
        DispatchRouteContext ctx, TransitDispatchArgs? args)
    {
        if (args is null) return ctx;
        return ctx with
        {
            Operation = args.Operation,
            OperationEquipmentId = args.Operation == DispatchOperationKind.ChangeFrame
                ? args.EquipmentId
                : null
        };
    }

    /// <summary>
    /// 操作角色策略：Transit 无额外限制；
    /// PalletReturn：From∈{Position,Frame} → AREA(PALLET_RETURN)；
    /// ChangeFrame：FRAME↔AREA(EMPTY_BUFFER|FULL_BUFFER)。
    /// </summary>
    private static bool MatchesOperationRole(DispatchOperationKind operation, DispatchRouteContext ctx)
        => operation switch
        {
            DispatchOperationKind.Transit => true,
            DispatchOperationKind.PalletReturn => MatchesPalletReturnRole(ctx),
            DispatchOperationKind.ChangeFrame => MatchesChangeFrameRole(ctx),
            _ => false
        };

    private static bool MatchesPalletReturnRole(DispatchRouteContext ctx)
    {
        var from = ctx.FromEndpoint;
        var to = ctx.ToEndpoint;
        if (from is null || to is null)
            return false;

        if (from.Kind is not (ManagedEndpointKind.Position or ManagedEndpointKind.Frame))
            return false;

        return to.Kind == ManagedEndpointKind.Area
               && string.Equals(to.LocName, "PALLET_RETURN", StringComparison.Ordinal);
    }

    private static bool MatchesChangeFrameRole(DispatchRouteContext ctx)
    {
        var from = ctx.FromEndpoint;
        var to = ctx.ToEndpoint;
        if (from is null || to is null)
            return false;

        // pull：FRAME → 缓冲 AREA；push：缓冲 AREA → FRAME
        if (from.Kind == ManagedEndpointKind.Frame && IsChangeFrameBufferArea(to))
            return true;
        if (IsChangeFrameBufferArea(from) && to.Kind == ManagedEndpointKind.Frame)
            return true;
        return false;
    }

    private static bool IsChangeFrameBufferArea(ManagedDispatchEndpoint ep)
        => ep.Kind == ManagedEndpointKind.Area
           && ep.LocName is "EMPTY_BUFFER" or "FULL_BUFFER";

    /// <summary>
    /// Grab：双端均为配置角色 AREA（LOAD/UNLOAD/缓冲/回收）；拒绝 POSITION/FRAME/未知角色。
    /// </summary>
    private static bool MatchesGrabRole(DispatchRouteContext ctx)
    {
        var from = ctx.FromEndpoint;
        var to = ctx.ToEndpoint;
        if (from is null || to is null)
            return false;

        return IsGrabAreaEndpoint(from) && IsGrabAreaEndpoint(to);
    }

    private static bool IsGrabAreaEndpoint(ManagedDispatchEndpoint ep)
        => ep.Kind == ManagedEndpointKind.Area
           && ManagedDispatchEndpoint.IsConfiguredAreaRole(ep.LocName);

    /// <summary>
    /// Identify / 盘点：单端必须为活动 FRAME（shelf/station 由 LOCATION_MAP.RcsType 表达）；拒绝 AREA/POSITION。
    /// </summary>
    private static bool MatchesIdentifyRole(DispatchRouteContext ctx)
    {
        var from = ctx.FromEndpoint;
        var to = ctx.ToEndpoint;
        if (from is null || to is null)
            return false;

        return from.Kind == ManagedEndpointKind.Frame
               && to.Kind == ManagedEndpointKind.Frame;
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

    private async Task<ReplayReservationHold> EnsureReplayReservationAsync(
        RcsTaskRow row, DispatchRouteContext route, CancellationToken ct)
    {
        var plan = ReplayReservation.Decide(
            row.Kind, row.TaskType, route.FromEndpoint?.Kind, route.ToEndpoint?.Kind);
        if (plan == ReplayReservationPlan.Skip)
            return ReplayReservationHold.Skipped();

        var frameId = plan == ReplayReservationPlan.Take
            ? route.SourceFrameId.Id
            : route.DestFrameId.Id;
        if (frameId is not long id || id <= 0)
            return ReplayReservationHold.Reject("料架未解析，拒绝重发");

        var hold = await ReplayReservation.EnsureAsync(
            _slots, row.RcsTaskId ?? "", plan, id, row.MaterialId, ct);
        if (!hold.Ok)
            _logger.LogWarning("重发预记失败 {TaskId}：{Msg}", row.RcsTaskId, hold.Failure?.Error);
        else if (hold.Created)
            _logger.LogInformation("重发已补预记 {TaskId} 方向={Plan} 料架={Frame}",
                row.RcsTaskId, plan, id);
        return hold;
    }

    private async Task RollbackReplayIfCreatedAsync(
        ReplayReservationHold hold, string taskId, CancellationToken ct)
    {
        if (!hold.Created) return;
        if (hold.IsTake)
            await _slots.RollbackTakeAsync(taskId, ct);
        else
            await _slots.RollbackAsync(taskId, ct);
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
