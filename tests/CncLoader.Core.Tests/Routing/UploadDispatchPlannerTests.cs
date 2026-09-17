using CncLoader.Communication.State;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace CncLoader.Core.Tests.Routing;

[TestFixture]
public sealed class UploadDispatchPlannerTests
{
    [Test]
    public async Task 无上料或中转绑定_WaitMaterial()
    {
        var planner = Create(uploadFrame: null, transitFrame: null, occupied: 2);
        var plan = await planner.ResolveUploadPlanAsync(Ctx(), CancellationToken.None);
        Assert.That(plan.Decision, Is.EqualTo(UploadDecision.WaitMaterial));
        Assert.That(plan.SourceFrameId, Is.Null);
    }

    [Test]
    public async Task 上料架账面无料_WaitMaterial()
    {
        var planner = Create(uploadFrame: 11, transitFrame: null, occupied: 0);
        var plan = await planner.ResolveUploadPlanAsync(Ctx(), CancellationToken.None);
        Assert.That(plan.Decision, Is.EqualTo(UploadDecision.WaitMaterial));
    }

    [Test]
    public async Task 缺LOCATION_MAP_Failed并告警()
    {
        var alarms = new NoopAlarms();
        var routes = new FixedRoutes { MissingPositionCell = true, MissingFrameCell = true };
        var planner = Create(uploadFrame: 11, transitFrame: null, occupied: 2, routes, alarms);
        var plan = await planner.ResolveUploadPlanAsync(Ctx(), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(plan.Decision, Is.EqualTo(UploadDecision.Failed));
            Assert.That(alarms.NotFoundCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task 有料且路由齐全_Queued并带回架与cell()
    {
        var planner = Create(uploadFrame: 11, transitFrame: null, occupied: 3);
        var plan = await planner.ResolveUploadPlanAsync(Ctx(), CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(plan.Decision, Is.EqualTo(UploadDecision.Queued));
            Assert.That(plan.SourceFrameId, Is.EqualTo(11));
            Assert.That(plan.From, Is.EqualTo("RACK-CELL-11"));
            Assert.That(plan.To, Is.EqualTo("CELL-EQ30-P1"));
        });
    }

    [Test]
    public async Task 无上料绑定则回退中转架()
    {
        var planner = Create(uploadFrame: null, transitFrame: 22, occupied: 1);
        var plan = await planner.ResolveUploadPlanAsync(Ctx(), CancellationToken.None);
        Assert.That(plan.SourceFrameId, Is.EqualTo(22));
        Assert.That(plan.Decision, Is.EqualTo(UploadDecision.Queued));
    }

    private static PositionContext Ctx()
        => new() { EquipmentId = 30, PositionId = 1, State = PositionState.WaitLoad };

    private static UploadDispatchPlanner Create(
        long? uploadFrame, long? transitFrame, int occupied,
        IRouteResolver? routes = null, NoopAlarms? alarms = null)
    {
        var equipment = new PlannerEquipment
        {
            UploadFrameId = uploadFrame,
            TransitFrameId = transitFrame
        };
        var slots = new PlannerSlots { Occupied = occupied };
        return new UploadDispatchPlanner(
            equipment, slots, routes ?? new FixedRoutes(), alarms ?? new NoopAlarms(),
            NullLogger.Instance, (eq, _) => Task.FromResult(new EquipmentFrameBindingIds(uploadFrame, null)));
    }
}
