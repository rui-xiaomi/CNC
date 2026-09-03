using CncLoader.Core.Plc;

namespace CncLoader.Core.Tests.Plc;

[TestFixture]
public sealed class PlcManagementIoSetTests
{
    [Test]
    public void 勾选写信号后_读面板与写面板都保留全部点位()
    {
        var points = new[]
        {
            Row(1, "有料", "D1004", isWrite: false, "工位1"),
            Row(2, "测试启动", "D1100", isWrite: true, "工位1"),
            Row(3, "门开关", "D1000", isWrite: false, null),
        };

        var read = PlcManagementIoSet.ForManualIo(points);
        var write = PlcManagementIoSet.ToWriteOptions(points, "内长宽 (EQ01)");

        Assert.Multiple(() =>
        {
            Assert.That(read.Select(p => p.RegisterAddress),
                Is.EquivalentTo(new[] { "D1004", "D1100", "D1000" }));
            Assert.That(write.Select(p => p.RegisterAddress),
                Is.EquivalentTo(new[] { "D1004", "D1100", "D1000" }));
            Assert.That(write.Select(w => w.DisplayName), Has.Some.Contain("测试启动"));
            Assert.That(write.Select(w => w.DisplayName), Has.Some.Contain("有料"));
            Assert.That(write.Select(w => w.DisplayName), Has.Some.Contain("门开关"));
        });
    }

    [Test]
    public void 写下拉文案_含机台与加工位()
    {
        var name = PlcManagementIoSet.FormatWriteDisplayName(
            "内长宽 (EQ01)", "工位1", "测试启动", "D1100");
        Assert.That(name, Is.EqualTo("内长宽 (EQ01) · 工位1 测试启动 (D1100)"));
    }

    private static PlcPointRow Row(long id, string label, string addr, bool isWrite, string? position)
        => new()
        {
            Id = id,
            PlcId = 1,
            EquipmentId = 1,
            SignalLabel = label,
            RegisterAddress = addr,
            IsWrite = isWrite,
            PositionName = position
        };
}
