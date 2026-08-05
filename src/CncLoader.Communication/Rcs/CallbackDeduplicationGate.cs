namespace CncLoader.Communication.Rcs;

/// <summary>回调去重协调结果（仅进程内；不改外部 ACK）。</summary>
internal enum CallbackDedupOutcome
{
    /// <summary>本请求为 leader，必要持久化已明确成功。</summary>
    Persisted,

    /// <summary>已成功处理过，或 follower 等到 leader 成功。</summary>
    Duplicate,

    /// <summary>leader/follower 所属处理失败；未提交 final seen。</summary>
    Failed,

    /// <summary>等待或处理被取消；未提交 final seen。</summary>
    Cancelled
}

/// <summary>单次协调结果。</summary>
internal readonly record struct CallbackDedupResult(
    CallbackDedupOutcome Outcome,
    string? ErrorMessage = null);

/// <summary>
/// RCS 回调 single-flight：in-flight 与 final seen 两态分离。
/// 仅在持久化明确成功后提交 final seen；失败/取消释放 in-flight。
/// final seen 使用 Dictionary+LinkedList 保持集合与 FIFO 顺序一致（P1-2）。
/// </summary>
internal sealed class CallbackDeduplicationGate
{
    private readonly int _finalSeenCapacity;
    private readonly Dictionary<string, TaskCompletionSource<CallbackDedupOutcome>> _inFlight = new();
    /// <summary>当前 live final seen：key → LinkedList 唯一节点。</summary>
    private readonly Dictionary<string, LinkedListNode<string>> _finalSeen = new();
    /// <summary>当前 live final seen 的 FIFO 顺序（无僵尸项）。</summary>
    private readonly LinkedList<string> _finalSeenOrder = new();
    private readonly object _gateLock = new();

    /// <summary>生产默认容量 4000。</summary>
    public CallbackDeduplicationGate() : this(4000) { }

    /// <summary>测试接缝：缩小 final seen 容量以复现淘汰时序；不得改变默认生产行为。</summary>
    internal CallbackDeduplicationGate(int finalSeenCapacity)
    {
        if (finalSeenCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(finalSeenCapacity), finalSeenCapacity, "必须 > 0");
        _finalSeenCapacity = finalSeenCapacity;
    }

    /// <summary>测试探针：live final seen 数量（Dictionary，持锁）。</summary>
    internal int ProbeFinalSeenCount
    {
        get { lock (_gateLock) return _finalSeen.Count; }
    }

    /// <summary>测试探针：live 顺序结构数量（LinkedList，持锁；须与 ProbeFinalSeenCount 一致）。</summary>
    internal int ProbeFinalSeenOrderCount
    {
        get { lock (_gateLock) return _finalSeenOrder.Count; }
    }

    /// <summary>
    /// 对 <paramref name="key"/> 执行占用 → 持久化 → 提交/释放。
    /// <paramref name="persistAsync"/> 返回 true=明确落库成功；false=显式未更新（不进 final seen）。
    /// leader 在锁外执行；follower 等待 leader 结果。
    /// </summary>
    public async Task<CallbackDedupResult> ExecuteAsync(
        string key,
        Func<CancellationToken, Task<bool>> persistAsync,
        CancellationToken ct)
    {
        TaskCompletionSource<CallbackDedupOutcome>? leaderTcs = null;
        Task<CallbackDedupOutcome>? followerWait = null;

        lock (_gateLock)
        {
            if (_finalSeen.ContainsKey(key))
                return new CallbackDedupResult(CallbackDedupOutcome.Duplicate);

            if (_inFlight.TryGetValue(key, out var existing))
            {
                followerWait = existing.Task;
            }
            else
            {
                leaderTcs = new TaskCompletionSource<CallbackDedupOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight[key] = leaderTcs;
            }
        }

        if (followerWait is not null)
            return await WaitFollowerAsync(followerWait, ct).ConfigureAwait(false);

        return await RunLeaderAsync(key, leaderTcs!, persistAsync, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 清除指定 taskId 的 push/scan final seen（redo/redispatch 后允许再收终态）。
    /// 同时从 Dictionary 与 LinkedList 删除节点；不碰 warn；不碰 in-flight。
    /// </summary>
    public void ForgetTask(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;
        var pushPrefix = $"push:{taskId}:";
        var scanPrefix = $"scan:{taskId}:";
        lock (_gateLock)
        {
            var remove = _finalSeen.Keys
                .Where(k => k.StartsWith(pushPrefix, StringComparison.Ordinal)
                            || k.StartsWith(scanPrefix, StringComparison.Ordinal))
                .ToList();
            foreach (var k in remove)
                RemoveFinalSeen_NoLock(k);
        }
    }

    private static async Task<CallbackDedupResult> WaitFollowerAsync(
        Task<CallbackDedupOutcome> leaderTask,
        CancellationToken ct)
    {
        try
        {
            var outcome = await leaderTask.WaitAsync(ct).ConfigureAwait(false);
            return MapFollower(outcome);
        }
        catch (OperationCanceledException)
        {
            // 仅取消 follower 等待；不得触碰 leader 的 in-flight。
            return new CallbackDedupResult(CallbackDedupOutcome.Cancelled);
        }
    }

    private async Task<CallbackDedupResult> RunLeaderAsync(
        string key,
        TaskCompletionSource<CallbackDedupOutcome> leaderTcs,
        Func<CancellationToken, Task<bool>> persistAsync,
        CancellationToken ct)
    {
        try
        {
            var applied = await persistAsync(ct).ConfigureAwait(false);
            if (!applied)
            {
                lock (_gateLock)
                {
                    _inFlight.Remove(key);
                }

                leaderTcs.TrySetResult(CallbackDedupOutcome.Failed);
                return new CallbackDedupResult(CallbackDedupOutcome.Failed, "任务不存在或状态未更新");
            }

            lock (_gateLock)
            {
                _inFlight.Remove(key);
                CommitFinalSeen_NoLock(key);
            }

            // TCS 在锁外完成，避免 continuation 持锁执行。
            leaderTcs.TrySetResult(CallbackDedupOutcome.Persisted);
            return new CallbackDedupResult(CallbackDedupOutcome.Persisted);
        }
        catch (OperationCanceledException)
        {
            lock (_gateLock)
            {
                _inFlight.Remove(key);
            }

            leaderTcs.TrySetResult(CallbackDedupOutcome.Cancelled);
            return new CallbackDedupResult(CallbackDedupOutcome.Cancelled);
        }
        catch (Exception ex)
        {
            lock (_gateLock)
            {
                _inFlight.Remove(key);
            }

            var safe = SanitizeError(ex);
            leaderTcs.TrySetResult(CallbackDedupOutcome.Failed);
            return new CallbackDedupResult(CallbackDedupOutcome.Failed, safe);
        }
    }

    /// <summary>
    /// 提交 live final seen：已存在则保持原节点与 FIFO 位置；新 key 尾插；超容量淘汰最老 live。
    /// </summary>
    private void CommitFinalSeen_NoLock(string key)
    {
        if (_finalSeen.ContainsKey(key))
            return;

        var node = _finalSeenOrder.AddLast(key);
        _finalSeen[key] = node;

        while (_finalSeenOrder.Count > _finalSeenCapacity)
        {
            var oldest = _finalSeenOrder.First;
            if (oldest is null) break;
            RemoveFinalSeen_NoLock(oldest.Value);
        }
    }

    /// <summary>同时从 Dictionary 与 LinkedList 删除指定 live key（幂等）。</summary>
    private void RemoveFinalSeen_NoLock(string key)
    {
        if (!_finalSeen.Remove(key, out var node))
            return;
        _finalSeenOrder.Remove(node);
    }

    private static CallbackDedupResult MapFollower(CallbackDedupOutcome leaderOutcome) => leaderOutcome switch
    {
        CallbackDedupOutcome.Persisted => new CallbackDedupResult(CallbackDedupOutcome.Duplicate),
        CallbackDedupOutcome.Duplicate => new CallbackDedupResult(CallbackDedupOutcome.Duplicate),
        CallbackDedupOutcome.Failed => new CallbackDedupResult(CallbackDedupOutcome.Failed),
        CallbackDedupOutcome.Cancelled => new CallbackDedupResult(CallbackDedupOutcome.Cancelled),
        _ => new CallbackDedupResult(CallbackDedupOutcome.Failed, "未知协调结果")
    };

    private static string SanitizeError(Exception ex)
    {
        var msg = ex.Message;
        if (string.IsNullOrEmpty(msg))
            return ex.GetType().Name;
        return msg.Length > 200 ? msg[..200] : msg;
    }
}
