using CncLoader.Common.Configuration;
using CncLoader.Common.Identity;
using CncLoader.Communication.Logging;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Plc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace CncLoader.Core.Tests.Communication;

/// <summary>P1-3：设备流水失败落库同源 30s 节流，PLC 离线时避免洪水式写库打满连接池。</summary>
[TestFixture]
public sealed class DeviceLogThrottleTests
{
    [Test]
    public void 同源失败记录_30s内_只落库一次()
    {
        var store = new CountingLogStore();
        var logger = CreateLogger(store);

        logger.Log(Fail(DeviceAction.Read, "D1004"));
        logger.Log(Fail(DeviceAction.Read, "D1004"));

        Assert.That(store.AppendCount, Is.EqualTo(1), "同源失败应节流");
    }

    [Test]
    public void 不同源失败记录_各自落库()
    {
        var store = new CountingLogStore();
        var logger = CreateLogger(store);

        logger.Log(Fail(DeviceAction.Read, "D1004"));
        logger.Log(Fail(DeviceAction.Read, "D1006"));

        Assert.That(store.AppendCount, Is.EqualTo(2), "不同地址失败应各自落库");
    }

    [Test]
    public void 成功读_默认不落库()
    {
        var store = new CountingLogStore();
        var logger = CreateLogger(store);

        logger.Log(new DeviceLogEntry
        {
            DeviceType = DeviceType.Plc, DeviceId = 1,
            Action = DeviceAction.Read, RegisterAddress = "D1004", Success = true
        });

        Assert.That(store.AppendCount, Is.EqualTo(0), "成功读默认不落库");
    }

    [Test]
    public void 写操作_不重复落库()
    {
        var store = new CountingLogStore();
        var logger = CreateLogger(store);

        logger.Log(new DeviceLogEntry
        {
            DeviceType = DeviceType.Plc, DeviceId = 1,
            Action = DeviceAction.Write, RegisterAddress = "D1100", Success = true
        });

        Assert.That(store.AppendCount, Is.EqualTo(0), "写操作由 PlcOperationService 先落库，此处跳过");
    }

    private static CompositeDeviceLogger CreateLogger(IDeviceLogStore store)
    {
        var options = Options.Create(new AppOptions
        {
            Logging = new LoggingOptions { PersistSuccessfulReads = false }
        });
        return new CompositeDeviceLogger(
            NullLogger<CompositeDeviceLogger>.Instance, store, options, new FixedUser("op"));
    }

    private static DeviceLogEntry Fail(DeviceAction action, string addr) => new()
    {
        DeviceType = DeviceType.Plc, DeviceId = 1, Action = action, RegisterAddress = addr, Success = false
    };

    private sealed class FixedUser : ICurrentUser
    {
        public string Name { get; }
        public FixedUser(string name) => Name = name;
    }

    private sealed class CountingLogStore : IDeviceLogStore
    {
#pragma warning disable CS0067
        public event EventHandler<DeviceLogRow>? LogAppended;
#pragma warning restore CS0067

        public int AppendCount { get; private set; }

        public Task<long> AppendAsync(DeviceLogEntry entry, CancellationToken ct = default)
        {
            AppendCount++;
            return Task.FromResult(1L);
        }

        public Task UpdateAsync(long id, string? response, bool success, int? costMs, string? error, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<DeviceLogRow>> GetRecentAsync(long? deviceId, int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeviceLogRow>>(Array.Empty<DeviceLogRow>());
        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
    }
}
