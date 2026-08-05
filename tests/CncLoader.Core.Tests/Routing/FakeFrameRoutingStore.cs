using CncLoader.Core.Abstractions;

namespace CncLoader.Core.Tests.Routing;

/// <summary>测试用 Frame 路由活动 Store。</summary>
internal sealed class FakeFrameRoutingStore : IFrameRoutingStore
{
    private readonly Dictionary<long, FrameRoutingSnapshot> _frames = new();
    private readonly Dictionary<long, int> _disableAfterFind = new();
    private readonly Dictionary<long, int> _findCounts = new();
    public int FindCallCount { get; private set; }
    public List<long> FindArgs { get; } = new();
    public Exception? ThrowOnNextFind { get; set; }

    public void Seed(long id, string state = "0", string? code = null)
        => _frames[id] = new FrameRoutingSnapshot(id, code ?? $"F{id}", state);

    public void SetState(long id, string state)
    {
        if (_frames.TryGetValue(id, out var f))
            _frames[id] = f with { State = state };
        else
            _frames[id] = new FrameRoutingSnapshot(id, $"F{id}", state);
    }

    public void Remove(long id) => _frames.Remove(id);

    /// <summary>第 N 次 FindAsync(frameId) 返回后将该 Frame STATE 置 1（Pre→Final TOCTOU）。</summary>
    public void DisableAfterFindCount(long frameId, int findCount)
        => _disableAfterFind[frameId] = findCount;

    public void ResetFindCount()
    {
        FindCallCount = 0;
        FindArgs.Clear();
    }

    public Task<FrameRoutingSnapshot?> FindAsync(long frameId, CancellationToken ct = default)
    {
        if (ThrowOnNextFind is { } ex)
        {
            ThrowOnNextFind = null;
            FindCallCount++;
            FindArgs.Add(frameId);
            throw ex;
        }

        FindCallCount++;
        FindArgs.Add(frameId);
        _findCounts.TryGetValue(frameId, out var n);
        n++;
        _findCounts[frameId] = n;

        var hit = _frames.TryGetValue(frameId, out var f) ? f : null;

        if (_disableAfterFind.TryGetValue(frameId, out var after) && n >= after)
            SetState(frameId, "1");

        return Task.FromResult(hit);
    }
}
