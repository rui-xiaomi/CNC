using CncLoader.Core.State;

namespace CncLoader.Core.Tests.State;

/// <summary>P0-3：启动对账 fail-closed 协调器契约（GREEN）。</summary>
[TestFixture]
public sealed class StartupReconciliationTests
{
    [Test]
    public async Task RunAsync_阶段一失败时_后续阶段不得执行()
    {
        var fake = PhaseFake.Create();
        fake.Fail(ReconcilePhase.One, new InvalidOperationException("① 查询未完结任务失败"));

        var result = await new StartupReconcileCoordinator().RunAsync(
            fake.PhaseOne, fake.PhaseOneB, fake.PhaseTwo, fake.PhaseThree);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False, "① 失败后本轮应对账失败");
            Assert.That(result.FailedPhase, Is.EqualTo(ReconcilePhase.One));
            Assert.That(fake.CallCount(ReconcilePhase.One), Is.EqualTo(1));
            Assert.That(fake.CallCount(ReconcilePhase.OneB), Is.Zero);
            Assert.That(fake.CallCount(ReconcilePhase.Two), Is.Zero);
            Assert.That(fake.CallCount(ReconcilePhase.Three), Is.Zero);
            Assert.That(fake.CallOrder, Is.EqualTo(new[] { ReconcilePhase.One }));
        });
    }

    [Test]
    public async Task RunAsync_阶段一b失败时_后续阶段不得执行()
    {
        var fake = PhaseFake.Create();
        fake.Fail(ReconcilePhase.OneB, new InvalidOperationException("①b 终态收口失败"));

        var result = await new StartupReconcileCoordinator().RunAsync(
            fake.PhaseOne, fake.PhaseOneB, fake.PhaseTwo, fake.PhaseThree);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailedPhase, Is.EqualTo(ReconcilePhase.OneB));
            Assert.That(fake.CallCount(ReconcilePhase.One), Is.EqualTo(1));
            Assert.That(fake.CallCount(ReconcilePhase.OneB), Is.EqualTo(1));
            Assert.That(fake.CallCount(ReconcilePhase.Two), Is.Zero);
            Assert.That(fake.CallCount(ReconcilePhase.Three), Is.Zero);
            Assert.That(fake.CallOrder, Is.EqualTo(new[] { ReconcilePhase.One, ReconcilePhase.OneB }));
        });
    }

    [Test]
    public async Task RunAsync_阶段二失败时_阶段三不得执行()
    {
        var fake = PhaseFake.Create();
        fake.Fail(ReconcilePhase.Two, new InvalidOperationException("② 陈旧预记回滚失败"));

        var result = await new StartupReconcileCoordinator().RunAsync(
            fake.PhaseOne, fake.PhaseOneB, fake.PhaseTwo, fake.PhaseThree);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailedPhase, Is.EqualTo(ReconcilePhase.Two));
            Assert.That(fake.CallCount(ReconcilePhase.Three), Is.Zero);
            Assert.That(fake.CallOrder, Is.EqualTo(new[]
            {
                ReconcilePhase.One, ReconcilePhase.OneB, ReconcilePhase.Two
            }));
        });
    }

    [Test]
    public async Task RunAsync_阶段三失败时_本轮应对账失败()
    {
        var fake = PhaseFake.Create();
        fake.Fail(ReconcilePhase.Three, new InvalidOperationException("③ PLC 账实核对失败"));

        var result = await new StartupReconcileCoordinator().RunAsync(
            fake.PhaseOne, fake.PhaseOneB, fake.PhaseTwo, fake.PhaseThree);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailedPhase, Is.EqualTo(ReconcilePhase.Three));
            Assert.That(result.FailureReason, Does.Contain("③"));
            Assert.That(fake.CallOrder, Is.EqualTo(new[]
            {
                ReconcilePhase.One, ReconcilePhase.OneB, ReconcilePhase.Two, ReconcilePhase.Three
            }));
        });
    }

    [Test]
    public async Task DecideIsReconciled_任一阶段失败后_应保持未开闸()
    {
        var fake = PhaseFake.Create();
        fake.ReturnFail(ReconcilePhase.One, "无法获得可信完整未完结任务集合");

        var round = await new StartupReconcileCoordinator().RunAsync(
            fake.PhaseOne, fake.PhaseOneB, fake.PhaseTwo, fake.PhaseThree);

        var isReconciled = StartupReconcileCoordinator.DecideIsReconciled(round);

        Assert.Multiple(() =>
        {
            Assert.That(round.Succeeded, Is.False);
            Assert.That(isReconciled, Is.False,
                "失败轮次后 IsReconciled 必须保持 false（镜像 StartAsync 不得无条件开闸）");
        });
    }

    [Test]
    public void MapQueryResult_QuerySuccess为false时_应判定对账阶段失败()
    {
        var phase = StartupReconcileCoordinator.MapQueryResult(
            querySuccess: false, failureReason: "queryTask 失败");

        Assert.Multiple(() =>
        {
            Assert.That(phase.Succeeded, Is.False,
                "①b Query.Success=false 必须视为对账失败，不得当作可继续");
            Assert.That(phase.Phase, Is.EqualTo(ReconcilePhase.OneB));
            Assert.That(phase.FailureReason, Is.Not.Null.And.Not.Empty);
        });
    }

    [Test]
    public async Task RunAsync_阶段一b返回显式失败时_后续阶段不得执行()
    {
        // Query.Success=false 经映射后的显式失败结果（与抛异常路径并列）
        var fake = PhaseFake.Create();
        fake.ReturnFail(ReconcilePhase.OneB, "queryTask Success=false");

        var result = await new StartupReconcileCoordinator().RunAsync(
            fake.PhaseOne, fake.PhaseOneB, fake.PhaseTwo, fake.PhaseThree);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.FailedPhase, Is.EqualTo(ReconcilePhase.OneB));
            Assert.That(fake.CallCount(ReconcilePhase.Two), Is.Zero);
            Assert.That(fake.CallCount(ReconcilePhase.Three), Is.Zero);
        });
    }

    /// <summary>手工 fake：记录阶段调用次数/顺序，并可注入异常或显式失败结果。</summary>
    private sealed class PhaseFake
    {
        private readonly Dictionary<ReconcilePhase, int> _counts = new()
        {
            [ReconcilePhase.One] = 0,
            [ReconcilePhase.OneB] = 0,
            [ReconcilePhase.Two] = 0,
            [ReconcilePhase.Three] = 0
        };
        private readonly List<ReconcilePhase> _order = new();
        private readonly Dictionary<ReconcilePhase, Exception> _throw = new();
        private readonly Dictionary<ReconcilePhase, string> _fail = new();

        public IReadOnlyList<ReconcilePhase> CallOrder => _order;

        public static PhaseFake Create() => new();

        public int CallCount(ReconcilePhase phase) => _counts[phase];

        public void Fail(ReconcilePhase phase, Exception ex) => _throw[phase] = ex;

        public void ReturnFail(ReconcilePhase phase, string reason) => _fail[phase] = reason;

        public Task<ReconcilePhaseResult> PhaseOne(CancellationToken ct) => Invoke(ReconcilePhase.One, ct);
        public Task<ReconcilePhaseResult> PhaseOneB(CancellationToken ct) => Invoke(ReconcilePhase.OneB, ct);
        public Task<ReconcilePhaseResult> PhaseTwo(CancellationToken ct) => Invoke(ReconcilePhase.Two, ct);
        public Task<ReconcilePhaseResult> PhaseThree(CancellationToken ct) => Invoke(ReconcilePhase.Three, ct);

        private Task<ReconcilePhaseResult> Invoke(ReconcilePhase phase, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _counts[phase]++;
            _order.Add(phase);

            if (_throw.TryGetValue(phase, out var ex))
                throw ex;
            if (_fail.TryGetValue(phase, out var reason))
                return Task.FromResult(ReconcilePhaseResult.Fail(phase, reason));
            return Task.FromResult(ReconcilePhaseResult.Ok(phase));
        }
    }
}
