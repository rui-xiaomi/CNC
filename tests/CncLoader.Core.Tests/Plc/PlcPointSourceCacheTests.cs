using CncLoader.Core.Abstractions;
using CncLoader.Data.Repositories;
using NUnit.Framework;

namespace CncLoader.Core.Tests.Plc;

/// <summary>P1-1：全量点位查询进程内缓存——轮询每 500ms 调 GetAllAsync，缓存后 TTL 内不重复查库；CRUD 失效立即刷新。</summary>
[TestFixture]
public sealed class PlcPointSourceCacheTests
{
    [Test]
    public async Task GetAllAsync_连续调用_仅首次查库()
    {
        var store = new CountingPointRoutingStore();
        var source = new PlcPointSource(store);

        var first = await source.GetAllAsync();
        var second = await source.GetAllAsync();

        Assert.Multiple(() =>
        {
            Assert.That(store.FindCalls, Is.EqualTo(1), "TTL 内第二次应命中缓存，不再查库");
            Assert.That(second, Is.SameAs(first), "缓存应返回同一列表实例");
        });
    }

    [Test]
    public async Task Invalidate后_重新查库()
    {
        var store = new CountingPointRoutingStore();
        var source = new PlcPointSource(store);

        await source.GetAllAsync();
        source.Invalidate();
        await source.GetAllAsync();

        Assert.That(store.FindCalls, Is.EqualTo(2), "失效后应立即重新查库");
    }

    [Test]
    public void Invalidate_无缓存时_不查库不抛异常()
    {
        var store = new CountingPointRoutingStore();
        var source = new PlcPointSource(store);

        source.Invalidate();

        Assert.That(store.FindCalls, Is.EqualTo(0));
    }

    private sealed class CountingPointRoutingStore : IPlcPointRoutingStore
    {
        public int FindCalls { get; private set; }

        public Task<IReadOnlyList<PlcPointRoutingRow>> FindAsync(
            long? plcId, long? equipmentId, CancellationToken ct = default)
        {
            FindCalls++;
            return Task.FromResult<IReadOnlyList<PlcPointRoutingRow>>(Array.Empty<PlcPointRoutingRow>());
        }
    }
}
