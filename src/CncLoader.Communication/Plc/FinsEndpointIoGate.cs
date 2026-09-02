using System.Collections.Concurrent;

namespace CncLoader.Communication.Plc;

/// <summary>
/// 同一物理 FINS 端点（IP:端口）上的互斥闸。
/// 共物理 PLC 时多台逻辑客户端各持一个 UdpClient，但欧姆龙 FINS/UDP
/// 对同一源节点通常只接受一笔在途命令；并发会丢应答并在读超时后取消。
/// </summary>
internal static class FinsEndpointIoGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim For(string host, int port) =>
        Gates.GetOrAdd($"{host}:{port}", static _ => new SemaphoreSlim(1, 1));
}
