using CncLoader.Core.Rcs;

namespace CncLoader.Core.Tests.Rcs;

[TestFixture]
public sealed class RcsDisplayLabelsTests
{
    [TestCase("COMPLETED", "ok")]
    [TestCase("DISPATCHED", "run")]
    [TestCase("EXECUTING", "run")]
    [TestCase("RUNNING", "run")]
    [TestCase("FAILED", "alarm")]
    [TestCase("CANCELED", "ng")]
    [TestCase("CREATED", "idle")]
    [TestCase("", "idle")]
    [TestCase(null, "idle")]
    [TestCase("UNKNOWN", "warn")]
    public void StateToBadge_任务态映射徽标(string? state, string badge)
        => Assert.That(RcsDisplayLabels.StateToBadge(state), Is.EqualTo(badge));
}
