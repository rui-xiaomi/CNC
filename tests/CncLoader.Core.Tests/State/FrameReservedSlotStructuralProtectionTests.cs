using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.Data;
using CncLoader.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace CncLoader.Core.Tests.State;

/// <summary>
/// P0-6 R19：真实 FrameService 改层/删架对 Reserved（非空）拒绝。
/// </summary>
[TestFixture]
public sealed class FrameReservedSlotStructuralProtectionTests
{
    private const long FrameId = 90;

    [Test]
    public void R19_含Reserved槽_改层须拒绝且不得ApplyUpdate()
    {
        var structure = new FakeFrameStructureStore(
            new FrameStructureRow(FrameId, "F1", "F1", "ID1", LayerTotal: 1, SlotsPerLayer: 2, State: "0"),
            nonEmptySlots: 1,
            activeBinds: 0);
        var sut = new FrameService(new UnusedDbContextFactory(), structure);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.UpdateFrameAsync(new FrameEditModel
            {
                Id = FrameId,
                Name = "F1",
                Code = "F1",
                IdentifyCode = "ID1",
                LayerTotal = 2,
                SlotsPerLayer = 2
            }, "tester"));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain("预记").Or.Contain("占用").Or.Contain("锁定"));
            Assert.That(structure.ApplyUpdateCount, Is.EqualTo(0), "拒绝后不得重建槽/改层");
            Assert.That(structure.SoftDeleteCount, Is.EqualTo(0));
            Assert.That(structure.LastNonEmptyCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task R19_含Reserved槽_删架校验与Delete须拒绝且不得软删()
    {
        var structure = new FakeFrameStructureStore(
            new FrameStructureRow(FrameId, "F1", "F1", "ID1", 1, 2, "0"),
            nonEmptySlots: 1,
            activeBinds: 0);
        var sut = new FrameService(new UnusedDbContextFactory(), structure);

        var check = await sut.CheckDeleteFrameAsync(FrameId);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.DeleteFrameAsync(FrameId, "tester"));

        Assert.Multiple(() =>
        {
            Assert.That(check.CanDelete, Is.False);
            Assert.That(check.Message, Does.Contain("预记").Or.Contain("占用"));
            Assert.That(ex!.Message, Is.EqualTo(check.Message));
            Assert.That(structure.SoftDeleteCount, Is.EqualTo(0));
            Assert.That(structure.ApplyUpdateCount, Is.EqualTo(0));
            Assert.That(structure.Frame!.State, Is.EqualTo("0"), "Frame 不得被软删");
        });
    }

    [Test]
    public async Task R19_生产静态_非空计数含Reserved语义_空架可改层()
    {
        // 回归：非空=0 时改层应放行到 ApplyUpdate（RebuildEmptySlots=true）
        var structure = new FakeFrameStructureStore(
            new FrameStructureRow(FrameId, "F1", "F1", "ID1", 1, 2, "0"),
            nonEmptySlots: 0,
            activeBinds: 0);
        var sut = new FrameService(new UnusedDbContextFactory(), structure);

        await sut.UpdateFrameAsync(new FrameEditModel
        {
            Id = FrameId,
            Name = "F1-new",
            Code = "F1",
            IdentifyCode = "ID1",
            LayerTotal = 2,
            SlotsPerLayer = 3
        }, "tester");

        Assert.Multiple(() =>
        {
            Assert.That(structure.ApplyUpdateCount, Is.EqualTo(1));
            Assert.That(structure.LastUpdate!.RebuildEmptySlots, Is.True);
            Assert.That(structure.LastUpdate.LayerTotal, Is.EqualTo(2));
        });
    }

    private sealed class FakeFrameStructureStore : IFrameStructureStore
    {
        public FrameStructureRow? Frame { get; private set; }
        public int NonEmptySlots { get; }
        public int ActiveBinds { get; }
        public int ApplyUpdateCount { get; private set; }
        public int SoftDeleteCount { get; private set; }
        public int LastNonEmptyCount { get; private set; }
        public FrameStructureUpdate? LastUpdate { get; private set; }

        public FakeFrameStructureStore(FrameStructureRow frame, int nonEmptySlots, int activeBinds)
        {
            Frame = frame;
            NonEmptySlots = nonEmptySlots;
            ActiveBinds = activeBinds;
        }

        public Task<FrameStructureRow?> FindAsync(long frameId, CancellationToken ct = default) =>
            Task.FromResult(Frame is { } f && f.Id == frameId ? f : null);

        public Task<int> CountNonEmptySlotsAsync(long frameId, CancellationToken ct = default)
        {
            LastNonEmptyCount = NonEmptySlots;
            return Task.FromResult(NonEmptySlots);
        }

        public Task<int> CountActiveBindsAsync(long frameId, CancellationToken ct = default) =>
            Task.FromResult(ActiveBinds);

        public Task ApplyUpdateAsync(FrameStructureUpdate update, CancellationToken ct = default)
        {
            ApplyUpdateCount++;
            LastUpdate = update;
            if (Frame is not null)
            {
                Frame = Frame with
                {
                    Name = update.Name,
                    Code = update.Code,
                    IdentifyCode = update.IdentifyCode,
                    LayerTotal = update.RebuildEmptySlots ? update.LayerTotal : Frame.LayerTotal,
                    SlotsPerLayer = update.RebuildEmptySlots ? update.SlotsPerLayer : Frame.SlotsPerLayer
                };
            }

            return Task.CompletedTask;
        }

        public Task SoftDeleteAsync(long frameId, string author, CancellationToken ct = default)
        {
            SoftDeleteCount++;
            if (Frame is not null)
                Frame = Frame with { State = "1" };
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedDbContextFactory : IDbContextFactory<CncDbContext>
    {
        public CncDbContext CreateDbContext() =>
            throw new InvalidOperationException("R19 改层/删架接缝测试不应走 IDbContextFactory");

        public Task<CncDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("R19 改层/删架接缝测试不应走 IDbContextFactory");
    }
}
