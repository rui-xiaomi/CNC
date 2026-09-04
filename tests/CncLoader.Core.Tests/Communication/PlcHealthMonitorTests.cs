using CncLoader.Common.Configuration;
using CncLoader.Communication.Polling;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace CncLoader.Core.Tests.Communication;

/// <summary>P0-5：PLC 持续失联超阈值 → 大声告警（级别严重）；恢复后下次失联重新告警；不负责重连。</summary>
[TestFixture]
public sealed class PlcHealthMonitorTests
{
    [Test]
    public async Task PLC持续失联超阈值_触发严重告警且不重复刷屏()
    {
        var store = new SignalStateStore();
        store.UpdateMachine(Offline(plcId: 10, equipmentId: 1));
        var alarms = new RecordingAlarms();
        var monitor = CreateMonitor(store, alarms, offlineAlarmAfterMs: 1, signalMaxAgeMs: 2500);

        await monitor.ProbeCheckOnceAsync();          // 首次：记录失联起点，不告警
        Assert.That(alarms.PlcCalls, Is.Empty);

        await Task.Delay(60);                          // 确保超过 1ms 阈值
        await monitor.ProbeCheckOnceAsync();          // 第二次：超阈值，告警一次
        await monitor.ProbeCheckOnceAsync();          // 第三次：仍失联，但已告警，不重复

        Assert.Multiple(() =>
        {
            Assert.That(alarms.PlcCalls, Has.Count.EqualTo(1), "每次失联只告警一次，不重复刷屏");
            Assert.That(alarms.PlcCalls[0].PlcId, Is.EqualTo(10));
            Assert.That(alarms.PlcCalls[0].Level, Is.EqualTo("2"), "失联按严重级别");
            Assert.That(alarms.PlcCalls[0].Message, Does.Contain("失联"));
        });
    }

    [Test]
    public async Task PLC在线_不告警()
    {
        var store = new SignalStateStore();
        store.UpdateMachine(new MachineStatus { EquipmentId = 1, PlcId = 10, PlcOnline = true, UpdatedAtUtc = DateTime.UtcNow });
        var alarms = new RecordingAlarms();
        var monitor = CreateMonitor(store, alarms, offlineAlarmAfterMs: 1, signalMaxAgeMs: 2500);

        await monitor.ProbeCheckOnceAsync();
        await monitor.ProbeCheckOnceAsync();

        Assert.That(alarms.PlcCalls, Is.Empty);
    }

    [Test]
    public async Task PLC恢复后再次失联_重新告警()
    {
        var store = new SignalStateStore();
        store.UpdateMachine(Offline(plcId: 10, equipmentId: 1));
        var alarms = new RecordingAlarms();
        var monitor = CreateMonitor(store, alarms, offlineAlarmAfterMs: 1, signalMaxAgeMs: 2500);

        await monitor.ProbeCheckOnceAsync();
        await Task.Delay(60);
        await monitor.ProbeCheckOnceAsync();
        Assert.That(alarms.PlcCalls, Has.Count.EqualTo(1));

        // 恢复在线 → 清除告警标记
        store.UpdateMachine(new MachineStatus { EquipmentId = 1, PlcId = 10, PlcOnline = true, UpdatedAtUtc = DateTime.UtcNow });
        await monitor.ProbeCheckOnceAsync();

        // 再次失联 → 重新告警
        store.UpdateMachine(Offline(plcId: 10, equipmentId: 1));
        await monitor.ProbeCheckOnceAsync();
        await Task.Delay(60);
        await monitor.ProbeCheckOnceAsync();

        Assert.That(alarms.PlcCalls, Has.Count.EqualTo(2), "恢复后再次失联须重新告警");
    }

    private static PlcHealthMonitor CreateMonitor(SignalStateStore store, RecordingAlarms alarms,
        int offlineAlarmAfterMs, int signalMaxAgeMs)
    {
        var options = Options.Create(new AppOptions
        {
            Plc = new PlcOptions { OfflineAlarmAfterMs = offlineAlarmAfterMs, SignalMaxAgeMs = signalMaxAgeMs }
        });
        return new PlcHealthMonitor(store, alarms, options, NullLogger<PlcHealthMonitor>.Instance);
    }

    private static MachineStatus Offline(long plcId, long equipmentId) => new()
    {
        EquipmentId = equipmentId,
        PlcId = plcId,
        PlcOnline = false,
        UpdatedAtUtc = DateTime.UtcNow
    };

    private sealed class RecordingAlarms : IAlarmEventService
    {
#pragma warning disable CS0067
        public event EventHandler<AlarmRow>? AlarmRaised;
        public event EventHandler? AlarmsChanged;
#pragma warning restore CS0067

        public List<(long PlcId, string Message, string Level)> PlcCalls { get; } = new();

        public Task RaisePlcAlarmAsync(long plcId, string message, string level = "1", CancellationToken ct = default)
        {
            PlcCalls.Add((plcId, message, level));
            return Task.CompletedTask;
        }

        public Task<long> RaiseRcsWarnAsync(string robotCode, string beginTime, string warnContent, string? taskCode, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<long> RaiseRcsTaskCanceledAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<long> RaiseRcsTaskNotFoundAsync(string rcsTaskId, string? reason = null, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<long> RaiseRcsRedoLimitAsync(string rcsTaskId, int maxRedo, string? reason = null, CancellationToken ct = default)
            => Task.FromResult(0L);
        public Task<IReadOnlyList<AlarmRow>> GetRecentAsync(int limit = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AlarmRow>>(Array.Empty<AlarmRow>());
        public Task<IReadOnlyList<AlarmRow>> GetAlarmsAsync(bool unhandledOnly, int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AlarmRow>>(Array.Empty<AlarmRow>());
        public Task MarkHandledAsync(long id, string author, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> DeleteAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> GetUnhandledCountAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
    }
}
