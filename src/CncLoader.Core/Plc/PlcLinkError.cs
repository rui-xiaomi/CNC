using System.Net.Sockets;

namespace CncLoader.Core.Plc;

/// <summary>
/// 把套接字/超时异常转成操作员能看懂的链路文案。
/// 取消 UDP Receive 后 Windows 常报 10022/995（「提供了一个无效的参数」），那是取消残留，不是参数写错。
/// </summary>
public static class PlcLinkError
{
    public static bool IsCanceledReceiveArtifact(Exception ex)
        => ex is SocketException se && IsCanceledReceiveArtifact(se);

    public static bool IsCanceledReceiveArtifact(SocketException ex)
        => ex.SocketErrorCode is SocketError.InvalidArgument
            or SocketError.OperationAborted
            or SocketError.Interrupted;

    public static string Describe(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is TimeoutException)
                return "通信超时，请检查网线与 PLC";
            if (e is SocketException se)
                return DescribeSocket(se);
            if (e is InvalidOperationException && e.Message.Contains("未连接", StringComparison.Ordinal))
                return "PLC 未连接";
        }

        return string.IsNullOrWhiteSpace(ex.Message) ? "PLC 通信异常" : ex.Message;
    }

    private static string DescribeSocket(SocketException se) => se.SocketErrorCode switch
    {
        SocketError.NetworkUnreachable or SocketError.NetworkDown
            => "网络不可达（网线断开或网段不通）",
        SocketError.HostUnreachable
            => "PLC 主机不可达",
        SocketError.TimedOut
            => "连接超时，请检查网线与 PLC",
        SocketError.ConnectionReset or SocketError.ConnectionAborted
            => "连接已断开",
        SocketError.ConnectionRefused
            => "PLC 拒绝连接",
        SocketError.InvalidArgument or SocketError.OperationAborted or SocketError.Interrupted
            => "通信中断（链路已断开）",
        _ => $"PLC 通信异常（{se.SocketErrorCode}）"
    };
}
