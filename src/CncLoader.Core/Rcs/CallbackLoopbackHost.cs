using System.Net;

namespace CncLoader.Core.Rcs;

/// <summary>
/// 本机回调探针目标：Kestrel 可能 ListenAnyIP，BoundHost 也可能是脏值。
/// 任意网卡 / 非法 IP / 环回一律落到 127.0.0.1，避免探针连不上本机监听。
/// </summary>
public static class CallbackLoopbackHost
{
    public static string Resolve(string? boundHost)
    {
        if (string.IsNullOrWhiteSpace(boundHost)) return "127.0.0.1";
        if (!IPAddress.TryParse(boundHost, out var ip)) return "127.0.0.1";
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(ip))
            return "127.0.0.1";
        return ip.ToString();
    }
}
