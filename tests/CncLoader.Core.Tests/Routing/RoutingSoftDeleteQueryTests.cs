using CncLoader.Common.Configuration;
using CncLoader.Communication.State;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Plc;
using CncLoader.Core.Rcs;
using CncLoader.Core.Signals;
using CncLoader.Core.State;
using CncLoader.Data;
using CncLoader.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 R1–R8：调度路由软删查询 GREEN。
/// 直接打真实 EquipmentConfigService / LocationMapService / PlcPointSource / PositionScheduler.ResolveLine。
/// Store fake 返回原始 STATE，不替 Service 过滤。
/// </summary>
[TestFixture]
public sealed class RoutingSoftDeleteQueryTests
{
    private const string Active = "0";
    private const string Disabled = "1";

    // ─── R1–R5：GetWorkLineByEquipmentAsync ───────────────────────────────

    [Test]
    public async Task R1_DisabledEquipment_GetWorkLine_ReturnsNull()
    {
        var store = new FakeEquipmentRoutingStore();
        store.Seed(lineId: 10, lineCode: "L10", lineState: Active,
            craftId: 20, craftNode: 1, craftState: Active,
            equipmentId: 30, equipmentState: Disabled);

        var svc = CreateEquipmentService(store);
        var result = await svc.GetWorkLineByEquipmentAsync(30);

        Assert.That(result, Is.Null, "Equipment STATE=1 时不得返回 WorkLine");
    }

    [Test]
    public async Task R2_DisabledCraft_GetWorkLine_ReturnsNull()
    {
        var store = new FakeEquipmentRoutingStore();
        store.Seed(lineId: 10, lineCode: "L10", lineState: Active,
            craftId: 20, craftNode: 1, craftState: Disabled,
            equipmentId: 30, equipmentState: Active);

        var svc = CreateEquipmentService(store);
        var result = await svc.GetWorkLineByEquipmentAsync(30);

        Assert.That(result, Is.Null, "Craft STATE=1 时不得返回 WorkLine");
    }

    [Test]
    public async Task R3_DisabledWorkLine_GetWorkLine_ReturnsNull()
    {
        var store = new FakeEquipmentRoutingStore();
        store.Seed(lineId: 10, lineCode: "L10", lineState: Disabled,
            craftId: 20, craftNode: 1, craftState: Active,
            equipmentId: 30, equipmentState: Active);

        var svc = CreateEquipmentService(store);
        var result = await svc.GetWorkLineByEquipmentAsync(30);

        Assert.That(result, Is.Null, "WorkLine STATE=1 时不得返回 WorkLine");
    }

    [Test]
    public async Task R4_MissingCraft_GetWorkLine_ReturnsNull_FailClosed()
    {
        var store = new FakeEquipmentRoutingStore();
        store.Equipments.Add(new EquipmentRoutingRow(30, CraftworkId: 999, State: Active));
        // Craft 999 不存在

        var svc = CreateEquipmentService(store);
        var result = await svc.GetWorkLineByEquipmentAsync(30);

        Assert.That(result, Is.Null);
        Assert.That(store.FindEquipmentCalls, Does.Contain(30L));
    }

    [Test]
    public async Task R4_MissingWorkLine_GetWorkLine_ReturnsNull_FailClosed()
    {
        var store = new FakeEquipmentRoutingStore();
        store.Equipments.Add(new EquipmentRoutingRow(30, CraftworkId: 20, State: Active));
        store.Crafts.Add(new CraftworkRoutingRow(20, WorkLineId: 888, CraftworkNode: 1, State: Active));
        // WorkLine 888 不存在

        var svc = CreateEquipmentService(store);
        var result = await svc.GetWorkLineByEquipmentAsync(30);

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task R4_UnknownEquipmentState_GetWorkLine_ReturnsNull_FailClosed()
    {
        var store = new FakeEquipmentRoutingStore();
        store.Seed(lineId: 10, lineCode: "L10", lineState: Active,
            craftId: 20, craftNode: 1, craftState: Active,
            equipmentId: 30, equipmentState: "x");

        var svc = CreateEquipmentService(store);
        var result = await svc.GetWorkLineByEquipmentAsync(30);

        Assert.That(result, Is.Null, "未知 STATE 须 fail-closed");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("x")]
    public async Task R4_EquipmentStateNullEmptyUnknown_GetWorkLine_ReturnsNull(string? state)
    {
        var store = new FakeEquipmentRoutingStore();
        store.Seed(lineId: 10, lineCode: "L10", lineState: Active,
            craftId: 20, craftNode: 1, craftState: Active,
            equipmentId: 30, equipmentState: state);

        Assert.That(await CreateEquipmentService(store).GetWorkLineByEquipmentAsync(30), Is.Null);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("x")]
    public async Task R4_CraftStateNullEmptyUnknown_GetWorkLine_ReturnsNull(string? state)
    {
        var store = new FakeEquipmentRoutingStore();
        store.Seed(lineId: 10, lineCode: "L10", lineState: Active,
            craftId: 20, craftNode: 1, craftState: state,
            equipmentId: 30, equipmentState: Active);

        Assert.That(await CreateEquipmentService(store).GetWorkLineByEquipmentAsync(30), Is.Null);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("x")]
    public async Task R4_WorkLineStateNullEmptyUnknown_GetWorkLine_ReturnsNull(string? state)
    {
        var store = new FakeEquipmentRoutingStore();
        store.Seed(lineId: 10, lineCode: "L10", lineState: state,
            craftId: 20, craftNode: 1, craftState: Active,
            equipmentId: 30, equipmentState: Active);

        Assert.That(await CreateEquipmentService(store).GetWorkLineByEquipmentAsync(30), Is.Null);
    }

    [Test]
    public async Task R5_AllActive_GetWorkLine_ReturnsRealWorkLine()
    {
        var store = new FakeEquipmentRoutingStore();
        store.Seed(lineId: 10, lineCode: "LINE-A", lineState: Active,
            craftId: 20, craftNode: 1, craftState: Active,
            equipmentId: 30, equipmentState: Active);

        var svc = CreateEquipmentService(store);
        var result = await svc.GetWorkLineByEquipmentAsync(30);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.WorkLineId, Is.EqualTo(10));
        Assert.That(result.LineCode, Is.EqualTo("LINE-A"));
    }

    // ─── R6：ResolveLine 禁止默认 LINE 回退 ───────────────────────────────

    [Test]
    public async Task R6_WhenGetWorkLineReturnsNull_ResolveLine_DoesNotFallbackToDefaultLine()
    {
        var equipment = new RecordingEquipmentConfigService
        {
            WorkLineByEquipment = _ => Task.FromResult<WorkLineRef?>(null)
        };
        var scheduler = CreateScheduler(equipment);

        var line = await scheduler.ProbeResolveLineAsync(42);

        Assert.That(line, Is.Null, "ResolveLine 在查询 null 时须返回不可用，不得回退 (1,\"LINE\")");
        Assert.That(scheduler.ProbeHasLineCache(42), Is.False, "不可用结果不得写入 _lineCache");
    }

    [Test]
    public async Task R6_WhenEquipmentDisabled_ResolveLine_IsUnavailable()
    {
        var store = new FakeEquipmentRoutingStore();
        store.Seed(lineId: 10, lineCode: "L10", lineState: Active,
            craftId: 20, craftNode: 1, craftState: Active,
            equipmentId: 30, equipmentState: Disabled);
        var equipment = CreateEquipmentService(store);
        var scheduler = CreateScheduler(equipment);

        var line = await scheduler.ProbeResolveLineAsync(30);

        Assert.That(line, Is.Null, "禁用机台 ResolveLine 须不可用");
        Assert.That(scheduler.ProbeHasLineCache(30), Is.False);
    }

    [Test]
    public async Task R6_AllActive_ResolveLine_CachesRealWorkLine()
    {
        var store = new FakeEquipmentRoutingStore();
        store.Seed(lineId: 10, lineCode: "LINE-A", lineState: Active,
            craftId: 20, craftNode: 1, craftState: Active,
            equipmentId: 30, equipmentState: Active);
        var scheduler = CreateScheduler(CreateEquipmentService(store));

        var line = await scheduler.ProbeResolveLineAsync(30);

        Assert.That(line, Is.Not.Null);
        Assert.That(line!.WorkLineId, Is.EqualTo(10));
        Assert.That(line.LineCode, Is.EqualTo("LINE-A"));
        Assert.That(scheduler.ProbeHasLineCache(30), Is.True);

        var again = await scheduler.ProbeResolveLineAsync(30);
        Assert.That(again, Is.EqualTo(line));
    }

    [Test]
    public async Task R6_WhenResolveLineUnavailable_UploadDispatch_DoesNotReserveOrDispatchOrWriteTestStart()
    {
        var equipment = new RecordingEquipmentConfigService
        {
            WorkLineByEquipment = _ => Task.FromResult<WorkLineRef?>(null),
            FrameBindingIds = _ => Task.FromResult(new EquipmentFrameBindingIds(50, null))
        };
        var slots = new CountingSlots { OccupiedOnFrame = 50 };
        var tasks = new CountingTaskService();
        var plc = new CountingPlcOps();
        var routes = new FixedUploadRoutes("FROM-A", "TO-B");
        var scheduler = CreateScheduler(equipment, slots, tasks, plc, routes);
        scheduler.ProbeMarkReconciled();
        scheduler.ProbeSeedUploadCandidate(30, 1);

        await scheduler.ProbeDispatchOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(slots.ReserveTakeCount, Is.EqualTo(0));
            Assert.That(tasks.DispatchTransitCount, Is.EqualTo(0));
            Assert.That(plc.WriteCount, Is.EqualTo(0));
        });
    }

    // ─── R7：NextProcess ─────────────────────────────────────────────────

    [Test]
    public async Task R7_NextEquipmentDisabled_NotReturned()
    {
        var store = SeedNextProcessGraph(
            nextEquipmentState: Disabled, nextCraftState: Active, workLineState: Active);
        var ids = await CreateEquipmentService(store).GetNextProcessEquipmentsAsync(1);
        Assert.That(ids, Does.Not.Contain(2L));
    }

    [Test]
    public async Task R7_NextCraftDisabled_NotReturned()
    {
        var store = SeedNextProcessGraph(
            nextEquipmentState: Active, nextCraftState: Disabled, workLineState: Active);
        var ids = await CreateEquipmentService(store).GetNextProcessEquipmentsAsync(1);
        Assert.That(ids, Is.Empty);
    }

    [Test]
    public async Task R7_ParentWorkLineDisabled_NextNotReturned()
    {
        var store = SeedNextProcessGraph(
            nextEquipmentState: Active, nextCraftState: Active, workLineState: Disabled);
        var ids = await CreateEquipmentService(store).GetNextProcessEquipmentsAsync(1);

        Assert.That(ids, Does.Not.Contain(2L),
            "父 WorkLine STATE=1 时 NextProcess 不得返回下游机台");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("x")]
    public async Task R7_ParentWorkLineUnknown_NextNotReturned(string? workLineState)
    {
        var store = SeedNextProcessGraph(
            nextEquipmentState: Active, nextCraftState: Active, workLineState: workLineState);
        var ids = await CreateEquipmentService(store).GetNextProcessEquipmentsAsync(1);
        Assert.That(ids, Is.Empty);
    }

    [Test]
    public async Task R7_AllActive_ReturnsNextEquipment()
    {
        var store = SeedNextProcessGraph(
            nextEquipmentState: Active, nextCraftState: Active, workLineState: Active);
        var ids = await CreateEquipmentService(store).GetNextProcessEquipmentsAsync(1);
        Assert.That(ids, Is.EquivalentTo(new[] { 2L }));
    }

    [Test]
    public async Task R7_Mixed_OnlyCompleteActiveChainReturned()
    {
        var store = new FakeEquipmentRoutingStore();
        store.WorkLines.Add(new WorkLineRoutingRow(10, "L10", Active));
        store.Crafts.Add(new CraftworkRoutingRow(100, 10, 1, Active));
        store.Equipments.Add(new EquipmentRoutingRow(1, 100, Active));
        // 下一工序：双机台混合
        store.Crafts.Add(new CraftworkRoutingRow(200, 10, 2, Active));
        store.Equipments.Add(new EquipmentRoutingRow(2, 200, Active));
        store.Equipments.Add(new EquipmentRoutingRow(3, 200, Disabled));

        var ids = await CreateEquipmentService(store).GetNextProcessEquipmentsAsync(1);

        Assert.That(ids, Is.EquivalentTo(new[] { 2L }));
    }

    [Test]
    public async Task R7_MixedParentLineState_OnlyActiveParentChainReturned()
    {
        // 活动父线体上的完整链应返回；禁用父线体上的下游不得混入（源在活动线）。
        var store = new FakeEquipmentRoutingStore();
        store.WorkLines.Add(new WorkLineRoutingRow(10, "L-ACTIVE", Active));
        store.WorkLines.Add(new WorkLineRoutingRow(11, "L-OFF", Disabled));
        store.Crafts.Add(new CraftworkRoutingRow(100, 10, 1, Active));
        store.Equipments.Add(new EquipmentRoutingRow(1, 100, Active));
        store.Crafts.Add(new CraftworkRoutingRow(200, 10, 2, Active));
        store.Equipments.Add(new EquipmentRoutingRow(2, 200, Active));
        // 禁用线体上的“伪下游”——不得被同名工序号误收
        store.Crafts.Add(new CraftworkRoutingRow(210, 11, 2, Active));
        store.Equipments.Add(new EquipmentRoutingRow(9, 210, Active));

        var ids = await CreateEquipmentService(store).GetNextProcessEquipmentsAsync(1);

        Assert.That(ids, Is.EquivalentTo(new[] { 2L }));
        Assert.That(ids, Does.Not.Contain(9L));
    }

    // ─── R8：保留 Map / Point / FrameBind 过滤 ───────────────────────────

    [Test]
    public async Task R8_LocationMap_Disabled_NotResolved()
    {
        var store = new FakeLocationMapRoutingStore();
        store.Rows.Add(new LocationMapRoutingRow(
            1, "POSITION", 30, 1, null, null, "CELL-OFF", "cell", Disabled));
        store.Rows.Add(new LocationMapRoutingRow(
            2, "POSITION", 30, 1, null, null, "CELL-ON", "cell", Active));

        var svc = new LocationMapService(new UnusedDbContextFactory(), store);
        var item = await svc.ResolvePositionAsync(30, 1, "cell");

        Assert.That(item, Is.Not.Null);
        Assert.That(item!.RcsCode, Is.EqualTo("CELL-ON"));
    }

    [Test]
    public async Task R8_LocationMap_OnlyDisabled_ReturnsNull()
    {
        var store = new FakeLocationMapRoutingStore();
        store.Rows.Add(new LocationMapRoutingRow(
            1, "AREA", null, null, null, "LOAD_AREA", "LOAD-OFF", "station", Disabled));

        var svc = new LocationMapService(new UnusedDbContextFactory(), store);
        var item = await svc.ResolveAreaAsync("LOAD_AREA");

        Assert.That(item, Is.Null);
    }

    [Test]
    public async Task R8_PlcPoint_Disabled_NotLoaded()
    {
        var store = new FakePlcPointRoutingStore();
        store.Rows.Add(new PlcPointRoutingRow(
            1, 1, 30, 1, "POS_HAS_MAT", "0", "D100", null, 1, 0, 1, Disabled));
        store.Rows.Add(new PlcPointRoutingRow(
            2, 1, 30, 1, "POS_HAS_MAT", "0", "D101", null, 1, 0, 1, Active));

        var src = new PlcPointSource(store);
        var all = await src.GetByEquipmentAsync(30);

        Assert.That(all.Count, Is.EqualTo(1));
        Assert.That(all[0].RegisterAddress, Is.EqualTo("D101"));
    }

    [Test]
    public async Task R8_FrameBind_Disabled_NotReturned()
    {
        var store = new FakeEquipmentRoutingStore();
        store.FrameBinds.Add(new FrameBindRoutingRow(1, FrameId: 50, EquipmentId: 30, FrameRole: "0", State: Disabled));
        store.FrameBinds.Add(new FrameBindRoutingRow(2, FrameId: 51, EquipmentId: 30, FrameRole: "0", State: Active));

        var svc = CreateEquipmentService(store);
        var frameId = await svc.GetFrameBindingByRoleAsync(30, FrameRole.Upload);
        var ids = await svc.GetFrameBindingIdsAsync(30);

        Assert.That(frameId, Is.EqualTo(51));
        Assert.That(ids.UploadFrameId, Is.EqualTo(51));
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    private static EquipmentConfigService CreateEquipmentService(IEquipmentRoutingStore store)
        => new(new UnusedDbContextFactory(), store);

    private static FakeEquipmentRoutingStore SeedNextProcessGraph(
        string? nextEquipmentState, string? nextCraftState, string? workLineState)
    {
        var store = new FakeEquipmentRoutingStore();
        store.WorkLines.Add(new WorkLineRoutingRow(10, "L10", workLineState));
        store.Crafts.Add(new CraftworkRoutingRow(100, 10, 1, Active)); // 源工序
        store.Equipments.Add(new EquipmentRoutingRow(1, 100, Active)); // 源机台
        store.Crafts.Add(new CraftworkRoutingRow(200, 10, 2, nextCraftState));
        store.Equipments.Add(new EquipmentRoutingRow(2, 200, nextEquipmentState));
        return store;
    }

    private static PositionScheduler CreateScheduler(
        IEquipmentConfigService equipment,
        ISlotAccountService? slots = null,
        IRcsTaskService? tasks = null,
        IPlcOperationService? plc = null,
        IRouteResolver? routes = null)
    {
        var options = Options.Create(new AppOptions
        {
            Rcs = new RcsOptions { SchedulerEnabled = false, ReconcileRetryIntervalMs = 5000 }
        });
        return new PositionScheduler(
            new SignalStateStore(),
            tasks ?? new NoopTaskService(),
            new NoopTaskStore(),
            new EmptyPoints(),
            plc ?? new NoopPlcOps(),
            routes ?? new NoopRoutes(),
            new PriorityDispatchQueue(),
            new NoopAlarms(),
            options,
            NullLogger<PositionScheduler>.Instance,
            new NoopWorkRecords(),
            equipment,
            slots ?? new NoopSlots(),
            new WorkLineOnlyRoutingValidator(equipment));
    }

    /// <summary>Store fake：原样返回 STATE，绝不自动过滤。</summary>
    private sealed class FakeEquipmentRoutingStore : IEquipmentRoutingStore
    {
        public List<EquipmentRoutingRow> Equipments { get; } = new();
        public List<CraftworkRoutingRow> Crafts { get; } = new();
        public List<WorkLineRoutingRow> WorkLines { get; } = new();
        public List<FrameBindRoutingRow> FrameBinds { get; } = new();
        public List<long> FindEquipmentCalls { get; } = new();

        public void Seed(
            long lineId, string lineCode, string? lineState,
            long craftId, long craftNode, string? craftState,
            long equipmentId, string? equipmentState)
        {
            WorkLines.Add(new WorkLineRoutingRow(lineId, lineCode, lineState));
            Crafts.Add(new CraftworkRoutingRow(craftId, lineId, craftNode, craftState));
            Equipments.Add(new EquipmentRoutingRow(equipmentId, craftId, equipmentState));
        }

        public Task<EquipmentRoutingRow?> FindEquipmentAsync(long equipmentId, CancellationToken ct = default)
        {
            FindEquipmentCalls.Add(equipmentId);
            return Task.FromResult(Equipments.FirstOrDefault(e => e.Id == equipmentId));
        }

        public Task<CraftworkRoutingRow?> FindCraftworkAsync(long craftworkId, CancellationToken ct = default)
            => Task.FromResult(Crafts.FirstOrDefault(c => c.Id == craftworkId));

        public Task<WorkLineRoutingRow?> FindWorkLineAsync(long workLineId, CancellationToken ct = default)
            => Task.FromResult(WorkLines.FirstOrDefault(l => l.Id == workLineId));

        public Task<IReadOnlyList<CraftworkRoutingRow>> FindCraftworksByWorkLineAsync(
            long workLineId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CraftworkRoutingRow>>(
                Crafts.Where(c => c.WorkLineId == workLineId).ToList());

        public Task<IReadOnlyList<EquipmentRoutingRow>> FindEquipmentsByCraftworkIdsAsync(
            IReadOnlyCollection<long> craftworkIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EquipmentRoutingRow>>(
                Equipments.Where(e => craftworkIds.Contains(e.CraftworkId)).ToList());

        public Task<IReadOnlyList<FrameBindRoutingRow>> FindFrameBindsByEquipmentAsync(
            long equipmentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FrameBindRoutingRow>>(
                FrameBinds.Where(b => b.EquipmentId == equipmentId).ToList());
    }

    private sealed class FakeLocationMapRoutingStore : ILocationMapRoutingStore
    {
        public List<LocationMapRoutingRow> Rows { get; } = new();

        public Task<IReadOnlyList<LocationMapRoutingRow>> FindByPositionAsync(
            long equipmentId, long? positionId, string rcsType, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LocationMapRoutingRow>>(Rows
                .Where(x => x.RcsType == rcsType && x.EquipmentId == equipmentId
                            && (positionId == null ? x.PositionId == null : x.PositionId == positionId))
                .ToList());

        public Task<IReadOnlyList<LocationMapRoutingRow>> FindByFrameAsync(
            long frameId, string rcsType, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LocationMapRoutingRow>>(Rows
                .Where(x => x.RcsType == rcsType && x.FrameId == frameId).ToList());

        public Task<IReadOnlyList<LocationMapRoutingRow>> FindByAreaAsync(
            string locName, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LocationMapRoutingRow>>(Rows
                .Where(x => x.LocType == "AREA" && x.LocName == locName).ToList());

        public Task<IReadOnlyList<LocationMapRoutingRow>> FindByRcsCodeAsync(
            string rcsCode, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LocationMapRoutingRow>>(Rows
                .Where(x => x.RcsCode == rcsCode).ToList());
    }

    private sealed class FakePlcPointRoutingStore : IPlcPointRoutingStore
    {
        public List<PlcPointRoutingRow> Rows { get; } = new();

        public Task<IReadOnlyList<PlcPointRoutingRow>> FindAsync(
            long? plcId, long? equipmentId, CancellationToken ct = default)
        {
            IEnumerable<PlcPointRoutingRow> q = Rows;
            if (plcId is not null) q = q.Where(p => p.PlcId == plcId);
            if (equipmentId is not null) q = q.Where(p => p.EquipmentId == equipmentId);
            return Task.FromResult<IReadOnlyList<PlcPointRoutingRow>>(q.ToList());
        }
    }

    private sealed class RecordingEquipmentConfigService : IEquipmentConfigService
    {
        public Func<long, Task<WorkLineRef?>> WorkLineByEquipment { get; set; }
            = _ => Task.FromResult<WorkLineRef?>(null);
        public Func<long, Task<EquipmentFrameBindingIds>> FrameBindingIds { get; set; }
            = _ => Task.FromResult(new EquipmentFrameBindingIds(null, null));

        public Task<WorkLineRef?> GetWorkLineByEquipmentAsync(long equipmentId, CancellationToken ct = default)
            => WorkLineByEquipment(equipmentId);

        public Task<IReadOnlyList<long>> GetNextProcessEquipmentsAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<long>>(Array.Empty<long>());
        public Task<bool> HasSubsequentProcessAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<EquipmentFrameBindingIds> GetFrameBindingIdsAsync(long equipmentId, CancellationToken ct = default)
            => FrameBindingIds(equipmentId);
        public Task<long?> GetFrameBindingByRoleAsync(long equipmentId, FrameRole role, CancellationToken ct = default)
            => Task.FromResult<long?>(null);
        public Task<IReadOnlyList<NamedOption>> GetCraftworkOptionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NamedOption>>(Array.Empty<NamedOption>());
        public Task<IReadOnlyList<EquipmentListItem>> GetByCraftAsync(long? craftworkId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EquipmentListItem>>(Array.Empty<EquipmentListItem>());
        public Task<IReadOnlyList<PositionItem>> GetPositionsAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PositionItem>>(Array.Empty<PositionItem>());
        public Task<IReadOnlyList<EquipmentFrameBinding>> GetFrameBindingsAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<EquipmentFrameBinding>>(Array.Empty<EquipmentFrameBinding>());
        public Task<IReadOnlyList<NamedOption>> GetPlcOptionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NamedOption>>(Array.Empty<NamedOption>());
        public Task<IReadOnlyList<NamedOption>> GetFrameOptionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NamedOption>>(Array.Empty<NamedOption>());
        public Task<string> SuggestNextNoAsync(CancellationToken ct = default) => Task.FromResult("EQ01");
        public Task<long> CreateEquipmentAsync(EquipmentCreateModel model, string author, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<EquipmentEditModel?> GetByIdAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult<EquipmentEditModel?>(null);
        public Task UpdateAsync(EquipmentEditModel model, string author, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<FrameBindingInfo>> GetBindingByFrameAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FrameBindingInfo>>(Array.Empty<FrameBindingInfo>());
        public Task SetFrameBindingAsync(long equipmentId, long? uploadFrameId, long? downloadFrameId, string author, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<DeleteCheckResult> CheckDeleteAsync(long equipmentId, CancellationToken ct = default)
            => Task.FromResult(new DeleteCheckResult(true, 0, ""));
        public Task DeleteAsync(long equipmentId, string author, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class UnusedDbContextFactory : IDbContextFactory<CncDbContext>
    {
        public CncDbContext CreateDbContext()
            => throw new InvalidOperationException("路由 RED 不得走 IDbContextFactory");
        public Task<CncDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("路由 RED 不得走 IDbContextFactory");
    }

    private sealed class EmptyPoints : IPlcPointSource
    {
        public Task<IReadOnlyList<PlcPointDefinition>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlcPointDefinition>>(Array.Empty<PlcPointDefinition>());
        public Task<IReadOnlyList<PlcPointDefinition>> GetByPlcAsync(long plcId, CancellationToken ct = default)
            => GetAllAsync(ct);
        public Task<IReadOnlyList<PlcPointDefinition>> GetByEquipmentAsync(long equipmentId, CancellationToken ct = default)
            => GetAllAsync(ct);
        public void Invalidate() { }
    }

    private sealed class NoopPlcOps : IPlcOperationService
    {
        public Task<IReadOnlyList<PlcReadResult>> ReadPointsAsync(long plcId, bool readOnlySignals = true, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlcReadResult>>(Array.Empty<PlcReadResult>());
        public Task<PlcReadResult> ReadRegisterAsync(long plcId, string registerAddress, int length, CancellationToken ct = default)
            => Task.FromResult(new PlcReadResult(null, "", null, registerAddress, 0, "", false, null, null));
        public Task<PlcWriteResult> WriteWithConfirmAsync(long plcId, string registerAddress, int value, string author, CancellationToken ct = default)
            => Task.FromResult(new PlcWriteResult(registerAddress, value, null, false, 0, "stub"));
        public Task<PlcWriteResult> VerifyWriteAsync(long plcId, string registerAddress, int expectedValue, CancellationToken ct = default)
            => Task.FromResult(new PlcWriteResult(registerAddress, expectedValue, null, false, 0, "stub"));
    }

    private sealed class CountingPlcOps : IPlcOperationService
    {
        public int WriteCount { get; private set; }
        public Task<IReadOnlyList<PlcReadResult>> ReadPointsAsync(long plcId, bool readOnlySignals = true, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PlcReadResult>>(Array.Empty<PlcReadResult>());
        public Task<PlcReadResult> ReadRegisterAsync(long plcId, string registerAddress, int length, CancellationToken ct = default)
            => Task.FromResult(new PlcReadResult(null, "", null, registerAddress, 0, "", false, null, null));
        public Task<PlcWriteResult> WriteWithConfirmAsync(long plcId, string registerAddress, int value, string author, CancellationToken ct = default)
        {
            WriteCount++;
            return Task.FromResult(new PlcWriteResult(registerAddress, value, null, true, 0, null));
        }
        public Task<PlcWriteResult> VerifyWriteAsync(long plcId, string registerAddress, int expectedValue, CancellationToken ct = default)
            => Task.FromResult(new PlcWriteResult(registerAddress, expectedValue, null, true, 0, null));
    }

    private sealed class NoopRoutes : IRouteResolver
    {
        public Task<(string from, string to)?> ResolveUploadAsync(long equipmentId, long positionId, CancellationToken ct = default)
            => Task.FromResult<(string, string)?>(null);
        public Task<(string from, string to)?> ResolveUnloadAsync(long equipmentId, long positionId, CancellationToken ct = default)
            => Task.FromResult<(string, string)?>(null);
        public Task<string?> ResolvePositionCellAsync(long equipmentId, long positionId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<string?> ResolveFrameCellAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FixedUploadRoutes : IRouteResolver
    {
        private readonly string _from;
        private readonly string _to;
        public FixedUploadRoutes(string from, string to) { _from = from; _to = to; }
        public Task<(string from, string to)?> ResolveUploadAsync(long equipmentId, long positionId, CancellationToken ct = default)
            => Task.FromResult<(string, string)?>((_from, _to));
        public Task<(string from, string to)?> ResolveUnloadAsync(long equipmentId, long positionId, CancellationToken ct = default)
            => Task.FromResult<(string, string)?>(null);
        public Task<string?> ResolvePositionCellAsync(long equipmentId, long positionId, CancellationToken ct = default)
            => Task.FromResult<string?>("POS-CELL");
        public Task<string?> ResolveFrameCellAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult<string?>("FRAME-CELL");
    }

    private sealed class CountingSlots : ISlotAccountService
    {
        public int OccupiedOnFrame { get; init; }
        public int ReserveTakeCount { get; private set; }
        public Task<int> RollbackStaleReservationsAsync(IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task<IReadOnlyList<CompletedPendingConfirm>> ListCompletedPendingConfirmAsync(
            IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CompletedPendingConfirm>>(Array.Empty<CompletedPendingConfirm>());
        public Task<ReservedSlot?> ReserveAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default)
            => Task.FromResult<ReservedSlot?>(null);
        public Task<bool> ConfirmAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> RollbackAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default)
        {
            ReserveTakeCount++;
            return Task.FromResult<ReservedSlot?>(new ReservedSlot(frameId, 1, 1, 1, null));
        }
        public Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<FrameOccupancy> GetOccupancyAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult(frameId == OccupiedOnFrame
                ? new FrameOccupancy(1, 1, 0, 10)
                : new FrameOccupancy(0, 0, 0, 10));
        public Task<SlotMutationResult> SetSlotAsync(long frameId, int slotNo, string? materialId, string slotState, string author, CancellationToken ct = default)
            => Task.FromResult(SlotMutationResult.From(SlotMutationStatus.NotFound, frameId, slotNo, null));
        public Task<SlotLocation?> LocateMaterialAsync(string materialId, CancellationToken ct = default)
            => Task.FromResult<SlotLocation?>(null);
        public Task<InventoryCorrectionResult> CorrectFromInventoryAsync(long frameId, int posStart, IReadOnlyList<string> products, CancellationToken ct = default)
            => Task.FromResult(InventoryCorrectionResult.Empty());
        public Task<IReadOnlyList<SlotRecord>> GetSlotsAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SlotRecord>>(Array.Empty<SlotRecord>());
    }

    private sealed class CountingTaskService : IRcsTaskService
    {
        public int DispatchTransitCount { get; private set; }
        public Task<RcsResult> DispatchTransitAsync(TransitDispatchArgs args, CancellationToken ct = default)
        {
            DispatchTransitCount++;
            return Task.FromResult(new RcsResult(true, 200, true, "ok", "", "{}", null, 1) { TaskId = args.TaskId ?? "t1" });
        }
        public Task<RcsResult> DispatchGrabAsync(GrabDispatchArgs args, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
        public Task<RcsResult> DispatchIdentifyAsync(IdentifyDispatchArgs args, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
        public Task<RcsResult> CancelAsync(string rcsTaskId, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
        public Task<RcsResult> RedoAsync(string rcsTaskId, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
        public Task<RcsResult> RedispatchAsync(string rcsTaskId, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
        public Task<RcsResult> AutoRedispatchAsync(string rcsTaskId, int maxRedoCount, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
        public Task<RcsResult> QueryAsync(QueryTaskRequest req, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
        public Task<IReadOnlyList<RcsTaskRow>> GetRecentTasksAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsTaskRow>>(Array.Empty<RcsTaskRow>());
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentMessagesAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryMessagesAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RcsResult> DispatchPalletReturnAsync(long equipmentId, long? positionId, string fromCode, string toCode,
            long workLineId, string lineCode, string author, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
    }

    private sealed class NoopAlarms : IAlarmEventService
    {
#pragma warning disable CS0067
        public event EventHandler<AlarmRow>? AlarmRaised;
        public event EventHandler? AlarmsChanged;
#pragma warning restore CS0067
        public Task RaisePlcAlarmAsync(long plcId, string message, string level = "1", CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> RaiseRcsWarnAsync(string robotCode, string beginTime, string warnContent, string? taskCode, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> RaiseRcsTaskCanceledAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> RaiseRcsTaskNotFoundAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> RaiseRcsRedoLimitAsync(string rcsTaskId, int maxRedo, string? reason = null, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<IReadOnlyList<AlarmRow>> GetRecentAsync(int limit = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AlarmRow>>(Array.Empty<AlarmRow>());
        public Task<IReadOnlyList<AlarmRow>> GetAlarmsAsync(bool unhandledOnly, int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AlarmRow>>(Array.Empty<AlarmRow>());
        public Task MarkHandledAsync(long id, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> DeleteAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> GetUnhandledCountAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class NoopWorkRecords : IWorkRecordService
    {
        public Task<long> RecordStartAsync(WorkRecordStartArgs args, CancellationToken ct = default) => Task.FromResult(0L);
        public Task RecordResultAsync(long recordId, string result, string? remark, CancellationToken ct = default) => Task.CompletedTask;
        public Task<WorkRecordRow?> FindOpenByPositionAsync(long equipmentId, long positionId, CancellationToken ct = default)
            => Task.FromResult<WorkRecordRow?>(null);
        public Task<IReadOnlyList<WorkRecordRow>> GetRecentAsync(int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkRecordRow>>(Array.Empty<WorkRecordRow>());
        public Task<WorkShiftStats> GetShiftStatsAsync(CancellationToken ct = default) => Task.FromResult(new WorkShiftStats(0, 0, 0));
    }

    private sealed class NoopSlots : ISlotAccountService
    {
        public Task<int> RollbackStaleReservationsAsync(IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task<IReadOnlyList<CompletedPendingConfirm>> ListCompletedPendingConfirmAsync(
            IReadOnlyCollection<string> activeTaskIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CompletedPendingConfirm>>(Array.Empty<CompletedPendingConfirm>());
        public Task<ReservedSlot?> ReserveAsync(long frameId, string taskId, string? materialId, CancellationToken ct = default)
            => Task.FromResult<ReservedSlot?>(null);
        public Task<bool> ConfirmAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> RollbackAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ReservedSlot?> ReserveTakeAsync(long frameId, string taskId, CancellationToken ct = default)
            => Task.FromResult<ReservedSlot?>(null);
        public Task<bool> ConfirmTakeAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> RollbackTakeAsync(string taskId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<FrameOccupancy> GetOccupancyAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult(new FrameOccupancy(0, 0, 0, 0));
        public Task<SlotMutationResult> SetSlotAsync(long frameId, int slotNo, string? materialId, string slotState, string author, CancellationToken ct = default)
            => Task.FromResult(SlotMutationResult.From(SlotMutationStatus.NotFound, frameId, slotNo, null));
        public Task<SlotLocation?> LocateMaterialAsync(string materialId, CancellationToken ct = default)
            => Task.FromResult<SlotLocation?>(null);
        public Task<InventoryCorrectionResult> CorrectFromInventoryAsync(long frameId, int posStart, IReadOnlyList<string> products, CancellationToken ct = default)
            => Task.FromResult(InventoryCorrectionResult.Empty());
        public Task<IReadOnlyList<SlotRecord>> GetSlotsAsync(long frameId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SlotRecord>>(Array.Empty<SlotRecord>());
    }

    private sealed class NoopTaskService : IRcsTaskService
    {
        public Task<RcsResult> DispatchTransitAsync(TransitDispatchArgs args, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
        public Task<RcsResult> DispatchGrabAsync(GrabDispatchArgs args, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
        public Task<RcsResult> DispatchIdentifyAsync(IdentifyDispatchArgs args, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
        public Task<RcsResult> CancelAsync(string rcsTaskId, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
        public Task<RcsResult> RedoAsync(string rcsTaskId, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
        public Task<RcsResult> RedispatchAsync(string rcsTaskId, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
        public Task<RcsResult> AutoRedispatchAsync(string rcsTaskId, int maxRedoCount, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
        public Task<RcsResult> QueryAsync(QueryTaskRequest req, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
        public Task<IReadOnlyList<RcsTaskRow>> GetRecentTasksAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsTaskRow>>(Array.Empty<RcsTaskRow>());
        public Task<IReadOnlyList<RcsMsgRow>> GetRecentMessagesAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task<IReadOnlyList<RcsMsgRow>> QueryMessagesAsync(RcsMsgQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsMsgRow>>(Array.Empty<RcsMsgRow>());
        public Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RcsResult> DispatchPalletReturnAsync(long equipmentId, long? positionId, string fromCode, string toCode,
            long workLineId, string lineCode, string author, CancellationToken ct = default)
            => Task.FromResult(RcsResult.Fail("", "noop"));
    }

    private sealed class NoopTaskStore : IRcsTaskStore
    {
        public Task<long> CreateAsync(RcsTaskRecord record, CancellationToken ct = default) => Task.FromResult(0L);
        public Task SetDispatchedAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> UpdateStateAsync(string rcsTaskId, string taskState, string? rcsStatus = null, string? error = null, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task IncrementRedoAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<AutoRedoClaimResult> TryClaimAutoRedoAsync(string rcsTaskId, int maxRedo, CancellationToken ct = default)
            => Task.FromResult(AutoRedoClaimResult.NotClaimable);
        public Task ConfirmCancelHandledAsync(string rcsTaskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<RcsTaskRow?> GetByTaskIdAsync(string rcsTaskId, CancellationToken ct = default)
            => Task.FromResult<RcsTaskRow?>(null);
        public Task<IReadOnlyList<RcsTaskRow>> GetRecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RcsTaskRow>>(Array.Empty<RcsTaskRow>());
        public Task<IReadOnlyList<string>> GetUnfinishedTaskIdsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
