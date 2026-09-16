using CncLoader.Communication.Plc;
using CncLoader.Communication.Polling;
using CncLoader.Core.Abstractions;
using CncLoader.Core.Signals;
using CncLoader.Core.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace CncLoader.Core.Tests.Communication;

/// <summary>P2-3：轮询按块批量读；批量读失败退回逐点读，点位不丢。</summary>
[TestFixture]
public sealed class PlcPollingBatchReadTests
{
    private const long Eq = 7;

    [Test]
    public async Task 同机台隔字读点位一次读完_值按偏移落信号仓()
    {
        var client = new FakePlcClient();
        client.Words[1000] = 1;
        client.Words[1002] = 2;
        client.Words[1004] = 1;
        var (polling, store) = Build(client);

        var count = await polling.PollOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(3));
            Assert.That(client.Reads, Is.EqualTo(new[] { ("D1000", 5) }), "三个隔字读点位须合并为一次读，写点位不读");
            Assert.That(store.GetReadings(Eq).ToDictionary(r => r.RegisterAddress, r => r.RawValue),
                Is.EquivalentTo(new Dictionary<string, int> { ["D1000"] = 1, ["D1002"] = 2, ["D1004"] = 1 }));
        });
    }

    [Test]
    public async Task 批量读失败_退回逐点读不丢点位()
    {
        var client = new FakePlcClient { FailMultiWordRead = true };
        client.Words[1000] = 1;
        client.Words[1002] = 2;
        client.Words[1004] = 1;
        var (polling, store) = Build(client);

        var count = await polling.PollOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(3));
            Assert.That(client.Reads, Is.EqualTo(new[] { ("D1000", 5), ("D1000", 1), ("D1002", 1), ("D1004", 1) }));
            Assert.That(store.GetReadings(Eq).Single(r => r.RegisterAddress == "D1002").RawValue, Is.EqualTo(2));
            Assert.That(store.GetMachine(Eq)!.PlcOnline, Is.True);
        });
    }

    [Test]
    public async Task 本轮读全失败_机台PlcOnline为false()
    {
        var client = new FakePlcClient { FailAllReads = true };
        var (polling, store) = Build(client);

        var count = await polling.PollOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(0));
            Assert.That(store.GetMachine(Eq)!.PlcOnline, Is.False);
        });
    }

    [Test]
    public async Task 不同PlcId一轮并行_各机台都读到()
    {
        var client1 = new FakePlcClient(1);
        client1.Words[1000] = 1;
        var client2 = new FakePlcClient(2);
        client2.Words[2000] = 1;
        var factory = new MapClientFactory(new Dictionary<long, IPlcClient>
        {
            [1] = client1,
            [2] = client2
        });
        var manager = new PlcConnectionManager(factory, NullLogger<PlcConnectionManager>.Instance);
        manager.Register(1, new PlcEndpoint("127.0.0.1", 1));
        manager.Register(2, new PlcEndpoint("127.0.0.1", 2));
        var points = new FixedPoints(new[]
        {
            Point("D1000", SignalKey.PosHasMat, positionId: 1, plcId: 1, equipmentId: 7),
            Point("D2000", SignalKey.PosHasMat, positionId: 1, plcId: 2, equipmentId: 8)
        });
        var store = new SignalStateStore();
        var polling = new PlcPollingService(points, manager, store, NullLogger<PlcPollingService>.Instance, 1000);

        var count = await polling.PollOnceAsync();

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(2));
            Assert.That(store.GetMachine(7)!.PlcOnline, Is.True);
            Assert.That(store.GetMachine(8)!.PlcOnline, Is.True);
            Assert.That(store.GetReadings(7).Single().RawValue, Is.EqualTo(1));
            Assert.That(store.GetReadings(8).Single().RawValue, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task 读到一半链路断开_机台PlcOnline为false()
    {
        var client = new FakePlcClient { DisconnectAfterReads = 1 };
        client.Words[1000] = 1;
        client.Words[1002] = 2;
        client.Words[1004] = 1;
        var (polling, store) = Build(client);

        await polling.PollOnceAsync();

        Assert.That(store.GetMachine(Eq)!.PlcOnline, Is.False);
    }

    private static (PlcPollingService Polling, SignalStateStore Store) Build(FakePlcClient client)
    {
        var manager = new PlcConnectionManager(new SingleClientFactory(client), NullLogger<PlcConnectionManager>.Instance);
        manager.Register(1, new PlcEndpoint("127.0.0.1", 1));
        var points = new FixedPoints(new[]
        {
            Point("D1000", SignalKey.PosHasMat, positionId: 1),
            Point("D1002", SignalKey.PosAllowLoad, positionId: 1),
            Point("D1004", SignalKey.Door, positionId: null),
            Point("D1100", SignalKey.PosTestStart, positionId: 1, isWrite: true)
        });
        var store = new SignalStateStore();
        return (new PlcPollingService(points, manager, store, NullLogger<PlcPollingService>.Instance, 1000), store);
    }

    private static PlcPointDefinition Point(
        string address, SignalKey signal, long? positionId, bool isWrite = false,
        long plcId = 1, long equipmentId = Eq)
        => new()
        {
            PlcId = plcId, EquipmentId = equipmentId, PositionId = positionId, Signal = signal,
            RegisterAddress = address, IsWrite = isWrite, OnValue = 1, OffValue = 2
        };

    private sealed class FakePlcClient : IPlcClient
    {
        public FakePlcClient(long plcId = 1) => PlcId = plcId;

        public Dictionary<int, int> Words { get; } = new();
        public List<(string Address, int Length)> Reads { get; } = new();
        public bool FailMultiWordRead { get; init; }
        public bool FailAllReads { get; init; }
        public int DisconnectAfterReads { get; init; }

        public long PlcId { get; }
        public PlcEndpoint Endpoint { get; } = new("127.0.0.1", 1);
        public PlcConnectionState State { get; private set; } = PlcConnectionState.Connected;
        public bool IsConnected => State == PlcConnectionState.Connected;
#pragma warning disable CS0067
        public event EventHandler<PlcConnectionStateChangedEventArgs>? ConnectionStateChanged;
#pragma warning restore CS0067

        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<bool> HeartbeatAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<int[]> ReadRegistersAsync(string registerAddress, int length, CancellationToken ct = default)
        {
            Reads.Add((registerAddress, length));
            if (FailAllReads) throw new TimeoutException("模拟断网超时");
            if (FailMultiWordRead && length > 1) throw new InvalidOperationException("模拟夹带字不可读");
            var start = RegisterAddress.ToRegisterIndex(registerAddress);
            var values = Enumerable.Range(start, length).Select(i => Words.GetValueOrDefault(i)).ToArray();
            if (DisconnectAfterReads > 0 && Reads.Count >= DisconnectAfterReads)
                State = PlcConnectionState.Faulted;
            return Task.FromResult(values);
        }

        public Task WriteRegisterAsync(string registerAddress, int value, CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class SingleClientFactory(IPlcClient client) : IPlcClientFactory
    {
        public IPlcClient Create(long plcId, PlcEndpoint endpoint) => client;
    }

    private sealed class MapClientFactory(IReadOnlyDictionary<long, IPlcClient> map) : IPlcClientFactory
    {
        public IPlcClient Create(long plcId, PlcEndpoint endpoint) => map[plcId];
    }

    private sealed class FixedPoints(IReadOnlyList<PlcPointDefinition> points) : IPlcPointSource
    {
        public Task<IReadOnlyList<PlcPointDefinition>> GetAllAsync(CancellationToken ct = default) => Task.FromResult(points);
        public Task<IReadOnlyList<PlcPointDefinition>> GetByPlcAsync(long plcId, CancellationToken ct = default) => GetAllAsync(ct);
        public Task<IReadOnlyList<PlcPointDefinition>> GetByEquipmentAsync(long equipmentId, CancellationToken ct = default) => GetAllAsync(ct);
        public void Invalidate() { }
    }
}
