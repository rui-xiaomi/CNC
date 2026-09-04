using CncLoader.Core.Signals;
using CncLoader.Core.State;
using NUnit.Framework;

namespace CncLoader.Core.Tests.State;

/// <summary>P0-1：信号/机台快照有效期判定。过期读值必须判 unknown，勿当最新值参与状态判定（fail-closed）。</summary>
[TestFixture]
public sealed class SignalFreshnessTests
{
    [Test]
    public void SignalReading_最近读值_视为新鲜()
    {
        var r = new SignalReading
        {
            Signal = SignalKey.PosHasMat,
            RegisterAddress = "D1004",
            On = false,
            ReadAtUtc = DateTime.UtcNow
        };
        Assert.That(r.IsFresh(TimeSpan.FromSeconds(2.5)), Is.True);
    }

    [Test]
    public void SignalReading_超过有效期_视为过期()
    {
        var r = new SignalReading
        {
            Signal = SignalKey.PosHasMat,
            RegisterAddress = "D1004",
            On = false,
            ReadAtUtc = DateTime.UtcNow.AddSeconds(-10)
        };
        Assert.That(r.IsFresh(TimeSpan.FromSeconds(2.5)), Is.False);
    }

    [Test]
    public void MachineStatus_最近更新_视为新鲜()
    {
        var m = new MachineStatus { EquipmentId = 1, PlcOnline = true, UpdatedAtUtc = DateTime.UtcNow };
        Assert.That(m.IsFresh(TimeSpan.FromSeconds(2.5)), Is.True);
    }

    [Test]
    public void MachineStatus_超过有效期_视为过期()
    {
        var m = new MachineStatus
        {
            EquipmentId = 1,
            PlcOnline = true,
            UpdatedAtUtc = DateTime.UtcNow.AddSeconds(-10)
        };
        Assert.That(m.IsFresh(TimeSpan.FromSeconds(2.5)), Is.False);
    }
}
