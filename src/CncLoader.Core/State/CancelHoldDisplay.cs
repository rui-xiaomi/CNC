namespace CncLoader.Core.State;

/// <summary>未确认取消占用的工位卡文案：前缀 + 任务号，供看板展示与跳转解析。</summary>
public static class CancelHoldDisplay
{
    public const string Prefix = "待确认取消";

    public static string Format(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return Prefix;
        return ids.Count == 1
            ? $"{Prefix} {ids[0]}"
            : $"{Prefix} {ids[0]} 等{ids.Count}单";
    }

    public static bool IsHold(string? detail)
        => detail is not null && detail.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>取出文案里的第一张任务号；多单时是最新那张。</summary>
    public static string? TryParseTaskId(string? detail)
    {
        if (!IsHold(detail)) return null;
        var rest = detail.AsSpan(Prefix.Length).Trim();
        if (rest.IsEmpty) return null;
        var space = rest.IndexOf(' ');
        var id = space < 0 ? rest : rest[..space];
        return id.IsEmpty ? null : id.ToString();
    }
}
