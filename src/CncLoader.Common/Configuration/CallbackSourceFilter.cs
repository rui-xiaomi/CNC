using System.Net;

namespace CncLoader.Common.Configuration;

/// <summary>
/// RCS 回调来源 IP 白名单（P0-4）。回调端点无鉴权，只能靠来源限制：
/// 同网段任意主机伪造 scanTaskStatus 可整架改写槽位账，伪造 pushTaskStatus 可触发自动 redo。
/// 启动校验与回调宿主共用同一解析，避免两处口径不一致。
/// </summary>
public static class CallbackSourceFilter
{
    /// <summary>
    /// 解析配置项；空白项忽略，非法项单独返回由启动校验 fail-closed。
    /// 只接受点分四段 IPv4 或含冒号的 IPv6：<see cref="IPAddress.TryParse(string?, out IPAddress?)"/> 会把 "13" 当成 0.0.0.13。
    /// </summary>
    public static (IReadOnlyList<IPAddress> Allowed, IReadOnlyList<string> Invalid) Parse(IEnumerable<string?>? entries)
    {
        var allowed = new List<IPAddress>();
        var invalid = new List<string>();
        foreach (var raw in entries ?? Array.Empty<string?>())
        {
            var s = raw?.Trim() ?? "";
            if (s.Length == 0) continue;
            var wellFormed = s.Contains(':') || s.Count(c => c == '.') == 3;
            if (wellFormed && IPAddress.TryParse(s, out var ip))
                allowed.Add(Normalize(ip));
            else
                invalid.Add(s);
        }
        return (allowed, invalid);
    }

    /// <summary>来源是否在白名单内（IPv4 映射的 IPv6 按 IPv4 比较）；来源未知一律拒绝。</summary>
    public static bool IsAllowed(IPAddress? remote, IReadOnlyCollection<IPAddress> allowed)
        => remote is not null && allowed.Contains(Normalize(remote));

    /// <summary>
    /// 是否拒绝该来源。白名单为空时一律拒绝（fail-closed）；非空时只放行名单内来源。
    /// </summary>
    public static bool IsRejected(IPAddress? remote, IReadOnlyCollection<IPAddress> allowed)
        => allowed.Count == 0 || !IsAllowed(remote, allowed);

    /// <summary>
    /// 白名单非空时补本机环回。现场「测试本机监听」从 127.0.0.1 POST，
    /// 只写 RCS IP 时否则会被 403；环回伪造仅本机进程能做。
    /// 名单为空不补（空名单已拒绝全部来源）。
    /// </summary>
    public static IReadOnlyList<IPAddress> EnsureLocalProbeAllowed(IReadOnlyList<IPAddress> allowed)
    {
        if (allowed.Count == 0) return allowed;
        var list = new List<IPAddress>(allowed.Count + 2);
        foreach (var ip in allowed)
            AddUnique(list, ip);
        AddUnique(list, IPAddress.Loopback);
        AddUnique(list, IPAddress.IPv6Loopback);
        return list;
    }

    private static void AddUnique(List<IPAddress> list, IPAddress ip)
    {
        var n = Normalize(ip);
        if (!list.Any(a => Normalize(a).Equals(n)))
            list.Add(n);
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
