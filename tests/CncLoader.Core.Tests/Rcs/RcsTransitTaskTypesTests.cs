using CncLoader.Core.Rcs;

namespace CncLoader.Core.Tests.Rcs;

[TestFixture]
public sealed class RcsTransitTaskTypesTests
{
    [Test]
    public void 上料_out()
        => Assert.That(RcsTransitTaskTypes.FromDispatch(RcsTaskKind.Transit, "0"), Is.EqualTo("out"));

    [Test]
    public void 下料_in()
        => Assert.That(RcsTransitTaskTypes.FromDispatch(RcsTaskKind.Transit, "1"), Is.EqualTo("in"));

    [Test]
    public void 转序_move()
        => Assert.That(RcsTransitTaskTypes.FromDispatch(RcsTaskKind.Transit, "2"), Is.EqualTo("move"));

    [Test]
    public void 换架即使TaskType为0或1_仍move()
    {
        Assert.That(RcsTransitTaskTypes.FromDispatch(RcsTaskKind.ChangeFrame, "0"), Is.EqualTo("move"));
        Assert.That(RcsTransitTaskTypes.FromDispatch(RcsTaskKind.ChangeFrame, "1"), Is.EqualTo("move"));
        Assert.That(RcsTransitTaskTypes.FromDispatch(RcsTaskKind.PalletReturn, "2"), Is.EqualTo("move"));
    }

    [Test]
    public void 落库重发_按Kind与TaskType还原()
    {
        Assert.That(RcsTransitTaskTypes.FromStored("transit", "0"), Is.EqualTo("out"));
        Assert.That(RcsTransitTaskTypes.FromStored("transit", "1"), Is.EqualTo("in"));
        Assert.That(RcsTransitTaskTypes.FromStored("change_frame", "1"), Is.EqualTo("move"));
    }
}
