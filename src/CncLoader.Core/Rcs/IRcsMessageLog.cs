namespace CncLoader.Core.Rcs;

/// <summary>RCS 双向报文流水落库（MAS_AUTO_RCS_MSG_LOG）。由通信层注入使用。</summary>
public interface IRcsMessageLog
{
    Task LogAsync(RcsMsgEntry entry, CancellationToken ct = default);

    /// <summary>最近 N 条报文（倒序），供 UI 展示。</summary>
    Task<IReadOnlyList<RcsMsgRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default);

    /// <summary>按条件服务端查询（方向/接口/taskId 模糊 + 条数上限），倒序。供报文流水筛选。</summary>
    Task<IReadOnlyList<RcsMsgRow>> QueryAsync(RcsMsgQuery query, CancellationToken ct = default);
}

/// <summary>报文流水查询条件（null/空表示不限）。</summary>
public sealed record RcsMsgQuery
{
    /// <summary>方向 OUT/IN；null 或空=全部。</summary>
    public string? Direction { get; init; }
    /// <summary>接口名（精确）；null 或空=全部。</summary>
    public string? Interface { get; init; }
    /// <summary>taskId 模糊匹配；null 或空=不限。</summary>
    public string? TaskId { get; init; }
    /// <summary>返回条数上限。</summary>
    public int Limit { get; init; } = 100;
}

/// <summary>报文流水写入项。</summary>
public sealed record RcsMsgEntry(
    string Direction,          // OUT / IN
    string? Interface,         // transitTask / pushTaskStatus ...
    string? Url,
    string? TaskId,
    string? RequestBody,
    string? ResponseBody,
    int? CostMs,
    bool Success,
    string? Error);

/// <summary>报文流水展示行。</summary>
public sealed record RcsMsgRow(
    long Id,
    DateTime Time,
    string Direction,
    string? Interface,
    string? TaskId,
    string? RequestBody,
    string? ResponseBody,
    int? CostMs,
    bool Success,
    string? Error);
