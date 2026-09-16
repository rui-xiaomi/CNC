using CncLoader.Core.Rcs;

namespace CncLoader.Core.Tests.Rcs;

/// <summary>P1-1：回调/轮询乱序时终态不被迟到的旧态覆盖。</summary>
[TestFixture]
public sealed class RcsTaskStateTransitionTests
{
    [TestCase(RcsTaskState.Completed, RcsTaskState.Executing, false)]
    [TestCase(RcsTaskState.Completed, RcsTaskState.Dispatched, false)]
    [TestCase(RcsTaskState.Completed, RcsTaskState.Failed, false)]
    [TestCase(RcsTaskState.Completed, RcsTaskState.Canceled, false)]
    [TestCase(RcsTaskState.Completed, RcsTaskState.Completed, true)]
    [TestCase(RcsTaskState.Canceled, RcsTaskState.Executing, false)]
    [TestCase(RcsTaskState.Canceled, RcsTaskState.Failed, false)]
    [TestCase(RcsTaskState.Canceled, RcsTaskState.Completed, true)]
    [TestCase(RcsTaskState.Failed, RcsTaskState.Completed, true)]
    [TestCase(RcsTaskState.Failed, RcsTaskState.Executing, true)]
    [TestCase(RcsTaskState.Dispatched, RcsTaskState.Executing, true)]
    [TestCase(RcsTaskState.Executing, RcsTaskState.Failed, true)]
    [TestCase(RcsTaskState.Created, RcsTaskState.Failed, true)]
    public void 迁移守卫(string current, string next, bool allowed)
        => Assert.That(RcsTaskStateTransition.IsAllowed(current, next), Is.EqualTo(allowed));
}
