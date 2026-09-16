using CncLoader.Core.Signals;

namespace CncLoader.Core.Tests.Communication;

/// <summary>P2-3：轮询批量读规划——隔字点位合并、超间隔/超长拆块、区码隔离、无法解析地址单独成块。</summary>
[TestFixture]
public sealed class RegisterReadPlannerTests
{
    [Test]
    public void 隔字排布的点位合并为一次读_按偏移取值()
    {
        var blocks = RegisterReadPlanner.Plan(new[] { Point("D1004"), Point("D1000"), Point("D1002") });

        Assert.Multiple(() =>
        {
            Assert.That(blocks, Has.Count.EqualTo(1));
            Assert.That(blocks[0].Address, Is.EqualTo("D1000"));
            Assert.That(blocks[0].Length, Is.EqualTo(5));
            Assert.That(blocks[0].Points.Select(p => (p.Point.RegisterAddress, p.Index)),
                Is.EqualTo(new[] { ("D1000", 0), ("D1002", 2), ("D1004", 4) }));
        });
    }

    [Test]
    public void 间隔超过上限_拆成两块()
    {
        var blocks = RegisterReadPlanner.Plan(new[] { Point("D1000"), Point("D1010") }, maxGap: 4);

        Assert.That(blocks.Select(b => (b.Address, b.Length)), Is.EqualTo(new[] { ("D1000", 1), ("D1010", 1) }));
    }

    [Test]
    public void 块长超过上限_拆块()
    {
        var blocks = RegisterReadPlanner.Plan(new[] { Point("D0"), Point("D2"), Point("D4") }, maxGap: 4, maxLength: 4);

        Assert.That(blocks.Select(b => (b.Address, b.Length)), Is.EqualTo(new[] { ("D0", 3), ("D4", 1) }));
    }

    [Test]
    public void 多字点位占满其长度()
    {
        var blocks = RegisterReadPlanner.Plan(new[] { Point("D100", length: 2), Point("D102") });

        Assert.Multiple(() =>
        {
            Assert.That(blocks.Single().Length, Is.EqualTo(3));
            Assert.That(blocks.Single().Points.Select(p => p.Index), Is.EqualTo(new[] { 0, 2 }));
        });
    }

    [Test]
    public void 不同区码不合并()
    {
        var blocks = RegisterReadPlanner.Plan(new[] { Point("D100"), Point("W100") });

        Assert.That(blocks.Select(b => b.Address), Is.EquivalentTo(new[] { "D100", "W100" }));
    }

    [Test]
    public void 地址无法解析_单独成块保留原地址()
    {
        var blocks = RegisterReadPlanner.Plan(new[] { Point("XYZ"), Point("D1") });

        Assert.That(blocks.Select(b => b.Address), Is.EquivalentTo(new[] { "XYZ", "D1" }));
    }

    private static PlcPointDefinition Point(string address, int length = 1)
        => new()
        {
            PlcId = 1, EquipmentId = 1, Signal = SignalKey.PosHasMat,
            RegisterAddress = address, DataLength = length, IsWrite = false
        };
}
