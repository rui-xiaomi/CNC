using CncLoader.Common.Configuration;
using CncLoader.Communication.Plc;
using NUnit.Framework;

namespace CncLoader.Core.Tests.Communication;

[TestFixture]
public sealed class LinkFaultAccumulatorTests
{
    [Test]
    public void 单次超时_未达阈值_不断链()
    {
        var acc = new LinkFaultAccumulator(3);
        Assert.That(acc.NoteTimeout(), Is.False);
    }

    [Test]
    public void 连续超时达阈值_断链()
    {
        var acc = new LinkFaultAccumulator(3);
        Assert.That(acc.NoteTimeout(), Is.False);
        Assert.That(acc.NoteTimeout(), Is.False);
        Assert.That(acc.NoteTimeout(), Is.True);
    }

    [Test]
    public void 成功读_清零后再超时_不断链()
    {
        var acc = new LinkFaultAccumulator(3);
        acc.NoteTimeout();
        acc.NoteTimeout();
        acc.NoteSuccess();
        Assert.That(acc.NoteTimeout(), Is.False);
    }

    [Test]
    public void 套接字级失败_立即断链()
    {
        var acc = new LinkFaultAccumulator(3);
        Assert.That(acc.NoteHardFailure(), Is.True);
    }

    [Test]
    public void 信号有效期_不低于读写超时加三轮轮询()
    {
        var plc = new PlcOptions
        {
            SignalMaxAgeMs = 2500,
            ReadWriteTimeoutMs = 5000,
            PollingIntervalMs = 500
        };
        Assert.That(plc.EffectiveSignalMaxAgeMs, Is.EqualTo(6500));
    }
}
