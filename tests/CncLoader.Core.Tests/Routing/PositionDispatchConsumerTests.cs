using CncLoader.Communication.State;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace CncLoader.Core.Tests.Routing;

[TestFixture]
public sealed class PositionDispatchConsumerTests
{
    [Test]
    public async Task 未开闸_不dequeue也不上料()
    {
        var queue = new MemoryDispatchQueue();
        queue.Enqueue(Item());
        var host = new RecordingDispatchHost { CanAutoDispatch = false };
        var consumer = new PositionDispatchConsumer(host, queue, NullLogger.Instance);

        await consumer.RunOnceAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(host.DispatchCount, Is.Zero);
            Assert.That(host.AllocateCount, Is.Zero);
        });
    }

    [Test]
    public async Task 有下料_先DispatchOne不再上料()
    {
        var queue = new MemoryDispatchQueue();
        queue.Enqueue(Item());
        var host = new RecordingDispatchHost { CanAutoDispatch = true };
        var consumer = new PositionDispatchConsumer(host, queue, NullLogger.Instance);

        await consumer.RunOnceAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(host.DispatchCount, Is.EqualTo(1));
            Assert.That(host.AllocateCount, Is.Zero);
        });
    }

    [Test]
    public async Task 队列空_才分配上料()
    {
        var host = new RecordingDispatchHost { CanAutoDispatch = true };
        var consumer = new PositionDispatchConsumer(host, new MemoryDispatchQueue(), NullLogger.Instance);

        await consumer.RunOnceAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(host.DispatchCount, Is.Zero);
            Assert.That(host.AllocateCount, Is.EqualTo(1));
        });
    }

    private static DispatchItem Item() => new()
    {
        EquipmentId = 30, PositionId = 1, Phase = PositionPhase.Unload,
        FromCode = "FROM", ToCode = "", WorkLineId = 10, LineCode = "LINE-A"
    };

    private sealed class RecordingDispatchHost : IPositionDispatchHost
    {
        public bool CanAutoDispatch { get; set; }
        public int DispatchCount { get; private set; }
        public int AllocateCount { get; private set; }

        public Task DispatchOneAsync(DispatchItem item, CancellationToken ct)
        {
            DispatchCount++;
            return Task.CompletedTask;
        }

        public Task<bool> AllocateUploadsAsync(CancellationToken ct)
        {
            AllocateCount++;
            return Task.FromResult(false);
        }
    }
}
