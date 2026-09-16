using CncLoader.Core.Rcs;
using CncLoader.Core.State;

namespace CncLoader.Core.Tests.State;

[TestFixture]
public sealed class ManualSlotClearPolicyTests
{
    [TestCase(null, null, true)]
    [TestCase("", null, true)]
    [TestCase("task-1", null, false)]
    [TestCase("task-1", "", false)]
    [TestCase("task-1", RcsTaskState.Completed, true)]
    [TestCase("task-1", RcsTaskState.Failed, false)]
    [TestCase("task-1", RcsTaskState.Canceled, false)]
    [TestCase("task-1", RcsTaskState.Created, false)]
    [TestCase("task-1", RcsTaskState.Dispatched, false)]
    [TestCase("task-1", RcsTaskState.Executing, false)]
    public void 仅终态或无主预记可强制置空(string? taskId, string? taskState, bool expected)
        => Assert.That(ManualSlotClearPolicy.AllowsForceClearReserved(taskId, taskState), Is.EqualTo(expected));
}
