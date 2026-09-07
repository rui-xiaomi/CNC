namespace CncLoader.Communication.Plc;

/// <summary>
/// 链路失败计数：单次 UDP/读超时不断链，连续达阈值或套接字级失败才 Faulted。
/// </summary>
internal sealed class LinkFaultAccumulator
{
    private readonly int _threshold;
    private int _consecutiveTimeouts;

    public LinkFaultAccumulator(int threshold)
        => _threshold = Math.Max(1, threshold);

    /// <summary>超时一次。返回 true 表示应置 Faulted。</summary>
    public bool NoteTimeout()
        => Interlocked.Increment(ref _consecutiveTimeouts) >= _threshold;

    /// <summary>套接字/IO 级失败，立即断链。</summary>
    public bool NoteHardFailure()
    {
        Interlocked.Exchange(ref _consecutiveTimeouts, _threshold);
        return true;
    }

    public void NoteSuccess()
        => Interlocked.Exchange(ref _consecutiveTimeouts, 0);
}
