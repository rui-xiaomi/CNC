using CncLoader.Core.Abstractions;
using CncLoader.Core.Rcs;
using CncLoader.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace CncLoader.Core.Tests.Routing;

/// <summary>P0-5 补充：Validator 原因区分 / 异常 fail-closed。</summary>
[TestFixture]
public sealed class RoutingAvailabilityValidatorTests
{
    [Test]
    public async Task Reason_Distinguishes_EquipmentDisabled()
    {
        var r = await ValidateWith(s => s.SetEquipmentState(30, "1"));
        Assert.That(r.Reason, Is.EqualTo(RoutingUnavailableReason.EquipmentDisabled));
        Assert.That(r.EntityKind, Is.EqualTo("Equipment"));
    }

    [Test]
    public async Task Reason_Distinguishes_CraftDisabled()
    {
        var r = await ValidateWith(s => s.SetCraftState(20, "1"));
        Assert.That(r.Reason, Is.EqualTo(RoutingUnavailableReason.CraftDisabled));
        Assert.That(r.EntityKind, Is.EqualTo("Craft"));
    }

    [Test]
    public async Task Reason_Distinguishes_WorkLineDisabled()
    {
        var r = await ValidateWith(s => s.SetWorkLineState(10, "1"));
        Assert.That(r.Reason, Is.EqualTo(RoutingUnavailableReason.WorkLineDisabled));
        Assert.That(r.EntityKind, Is.EqualTo("WorkLine"));
    }

    [Test]
    public async Task Validator_Exception_Returns_ConfigurationUnavailable()
    {
        var boom = new BoomEquipmentRoutingStore();
        var eq = new TracingEquipmentConfigService(new MutableEquipmentRoutingStore(), new CallTrace());
        var v = new RoutingAvailabilityValidator(boom, eq, NullLogger<RoutingAvailabilityValidator>.Instance);
        var r = await v.ValidateAsync(new DispatchRouteContext
        {
            SourceEquipmentId = 1,
            FromCode = "A",
            ToCode = "B"
        });
        Assert.That(r.IsAvailable, Is.False);
        Assert.That(r.Reason, Is.EqualTo(RoutingUnavailableReason.ConfigurationUnavailable));
    }

    private static async Task<RoutingAvailabilityResult> ValidateWith(Action<MutableEquipmentRoutingStore> mutate)
    {
        var store = new MutableEquipmentRoutingStore();
        store.SeedActiveChain(10, "L", 20, 1, 30);
        mutate(store);
        var eq = new TracingEquipmentConfigService(store, new CallTrace());
        var v = new RoutingAvailabilityValidator(store, eq, NullLogger<RoutingAvailabilityValidator>.Instance);
        var r = await v.ValidateAsync(new DispatchRouteContext
        {
            SourceEquipmentId = 30,
            FromCode = "A",
            ToCode = "B",
            RequiresResolvedCells = true
        });
        Assert.That(r.IsAvailable, Is.False);
        return r;
    }

    private sealed class BoomEquipmentRoutingStore : IEquipmentRoutingStore
    {
        public Task<EquipmentRoutingRow?> FindEquipmentAsync(long equipmentId, CancellationToken ct = default)
            => throw new InvalidOperationException("store down");
        public Task<CraftworkRoutingRow?> FindCraftworkAsync(long craftworkId, CancellationToken ct = default)
            => throw new InvalidOperationException("store down");
        public Task<WorkLineRoutingRow?> FindWorkLineAsync(long workLineId, CancellationToken ct = default)
            => throw new InvalidOperationException("store down");
        public Task<IReadOnlyList<CraftworkRoutingRow>> FindCraftworksByWorkLineAsync(long workLineId, CancellationToken ct = default)
            => throw new InvalidOperationException("store down");
        public Task<IReadOnlyList<EquipmentRoutingRow>> FindEquipmentsByCraftworkIdsAsync(
            IReadOnlyCollection<long> craftworkIds, CancellationToken ct = default)
            => throw new InvalidOperationException("store down");
        public Task<IReadOnlyList<FrameBindRoutingRow>> FindFrameBindsByEquipmentAsync(
            long equipmentId, CancellationToken ct = default)
            => throw new InvalidOperationException("store down");
    }
}
