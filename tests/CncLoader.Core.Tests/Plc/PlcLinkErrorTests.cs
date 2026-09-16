using System.Net.Sockets;
using CncLoader.Core.Plc;

namespace CncLoader.Core.Tests.Plc;

[TestFixture]
public sealed class PlcLinkErrorTests
{
    [Test]
    public void 超时_操作员文案不含套接字原文()
    {
        var text = PlcLinkError.Describe(new TimeoutException("FINS 应答超时（5000ms）"));
        Assert.That(text, Is.EqualTo("通信超时，请检查网线与 PLC"));
    }

    [Test]
    public void 网络不可达_映射为断网文案()
    {
        var text = PlcLinkError.Describe(new SocketException((int)SocketError.NetworkUnreachable));
        Assert.That(text, Is.EqualTo("网络不可达（网线断开或网段不通）"));
    }

    [Test]
    public void 取消残留10022_不是参数无效()
    {
        var ex = new SocketException((int)SocketError.InvalidArgument);
        Assert.Multiple(() =>
        {
            Assert.That(PlcLinkError.IsCanceledReceiveArtifact(ex), Is.True);
            Assert.That(PlcLinkError.Describe(ex), Is.EqualTo("通信中断（链路已断开）"));
            Assert.That(PlcLinkError.Describe(ex), Does.Not.Contain("无效的参数"));
        });
    }
}
