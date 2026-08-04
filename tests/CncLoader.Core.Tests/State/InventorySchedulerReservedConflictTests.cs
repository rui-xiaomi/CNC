using System.IO;
using System.Reflection;
using CncLoader.Common.Configuration;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Config;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using CncLoader.Communication.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CncLoader.Core.Tests.State;

/// <summary>
/// P0-6 R12：后台 <see cref="InventorySchedulerService"/> 不得弹用户 Growl。
/// 架构上不注入 <see cref="IUserNotificationService"/> —— 边界契约允许直接 PASS。
/// </summary>
[TestFixture]
public sealed class InventorySchedulerReservedConflictTests
{
    [Test]
    public void R12_后台调度不依赖IUserNotificationService且无Growl引用()
    {
        var ctor = typeof(InventorySchedulerService).GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(paramTypes, Does.Not.Contain(typeof(IUserNotificationService)));
            var src = Path.GetFullPath(Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "..", "..", "..", "..", "..",
                "src", "CncLoader.Communication", "State", "InventorySchedulerService.cs"));
            Assert.That(File.Exists(src), Is.True, src);
            var text = File.ReadAllText(src);
            Assert.That(text, Does.Not.Contain("IUserNotificationService"));
            Assert.That(text, Does.Not.Contain("Growl"));
            Assert.That(text, Does.Not.Contain("HandyControl"));
        });
    }

    [Test]
    public async Task R12_含ReservationConflict的完成事件_不抛异常不中止()
    {
        var inventory = new FakeInventory();
        var logger = new CapturingLogger();
        var scheduler = new InventorySchedulerService(
            new FakeFrames(),
            inventory,
            new FakeQueue(),
            new FakeChangeFrame(),
            Options.Create(new AppOptions
            {
                Rcs = new RcsOptions { InventoryAutoEnabled = true, InventoryIntervalMinutes = 60 }
            }),
            logger);

        await scheduler.StartAsync(CancellationToken.None);

        var correction = new InventoryCorrectionResult(
            InventoryCorrectionStatus.Completed,
            RequestedCount: 2,
            UpdatedCount: 1,
            UnchangedCount: 0,
            ReservationConflictCount: 1,
            NotFoundCount: 0,
            ConflictSlots: Array.Empty<SlotMutationSnapshot>());

        Assert.DoesNotThrow(() => inventory.RaiseCompleted(new InventoryResultEvent(
            7, "task-auto-1", "COMPLETED", "F-A", new[] { "A", "B" }, 1, null, correction)));

        await scheduler.StopAsync(CancellationToken.None);
        await scheduler.DisposeAsync();

        Assert.That(logger.Errors, Is.Empty);
    }

    private sealed class FakeInventory : IInventoryService
    {
        public event EventHandler<InventoryResultEvent>? InventoryCompleted;
        public void RaiseCompleted(InventoryResultEvent e) => InventoryCompleted?.Invoke(this, e);
        public Task<string> StartInventoryAsync(long frameId, int posStart, int count, string author, CancellationToken ct = default)
            => Task.FromResult("task-auto-1");
        public IReadOnlyList<InventoryTaskInfo> GetActiveInventories() => Array.Empty<InventoryTaskInfo>();
    }

    private sealed class FakeFrames : IFrameService
    {
        public Task<IReadOnlyList<FrameListItem>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FrameListItem>>(Array.Empty<FrameListItem>());
        public Task<FrameDetail?> GetDetailAsync(long frameId, CancellationToken ct = default) => Task.FromResult<FrameDetail?>(null);
        public Task<long> CreateFrameAsync(FrameCreateModel model, string author, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<IReadOnlyList<NamedOption>> GetEquipmentOptionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NamedOption>>(Array.Empty<NamedOption>());
        public Task BindEquipmentAsync(long frameId, long equipmentId, string roleCode, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task UnbindAsync(long bindId, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<FrameEditModel?> GetFrameForEditAsync(long id, CancellationToken ct = default) => Task.FromResult<FrameEditModel?>(null);
        public Task UpdateFrameAsync(FrameEditModel model, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<DeleteCheckResult> CheckDeleteFrameAsync(long id, CancellationToken ct = default)
            => Task.FromResult(new DeleteCheckResult(true, 0, ""));
        public Task DeleteFrameAsync(long id, string author, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeQueue : IDispatchQueue
    {
        public int Count => 0;
        public void Enqueue(DispatchItem item) { }
        public DispatchItem? Dequeue() => null;
        public void Clear() { }
    }

    private sealed class FakeChangeFrame : IChangeFrameOrchestrator
    {
        public event EventHandler<ChangeFrameProgressEvent>? ProgressChanged
        {
            add { }
            remove { }
        }

        public Task<string> ChangeFrameAsync(long equipmentId, FrameRole role, string author, CancellationToken ct = default)
            => Task.FromResult("");

        public IReadOnlyList<ChangeFrameProgressEvent> GetActiveTransactions() =>
            Array.Empty<ChangeFrameProgressEvent>();
    }

    private sealed class CapturingLogger : ILogger<InventorySchedulerService>
    {
        public List<string> Errors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                Errors.Add(formatter(state, exception));
        }
    }
}
