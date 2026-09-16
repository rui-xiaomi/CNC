using System.Net;
using CncLoader.Common.Configuration;

namespace CncLoader.Core.Tests.Configuration;

/// <summary>P0-4：RCS 回调来源白名单解析与匹配。</summary>
[TestFixture]
public sealed class CallbackSourceFilterTests
{
    [Test]
    public void 白名单内IPv4放行_名单外拒绝()
    {
        var (allowed, invalid) = CallbackSourceFilter.Parse(new[] { "192.168.250.13" });

        Assert.Multiple(() =>
        {
            Assert.That(invalid, Is.Empty);
            Assert.That(CallbackSourceFilter.IsAllowed(IPAddress.Parse("192.168.250.13"), allowed), Is.True);
            Assert.That(CallbackSourceFilter.IsAllowed(IPAddress.Parse("192.168.250.99"), allowed), Is.False);
        });
    }

    [Test]
    public void IPv4映射IPv6来源_按IPv4比较()
    {
        var (allowed, _) = CallbackSourceFilter.Parse(new[] { "192.168.250.13" });
        var mapped = IPAddress.Parse("192.168.250.13").MapToIPv6();

        Assert.That(CallbackSourceFilter.IsAllowed(mapped, allowed), Is.True);
    }

    [Test]
    public void 来源未知_一律拒绝()
    {
        var (allowed, _) = CallbackSourceFilter.Parse(new[] { "192.168.250.13" });

        Assert.That(CallbackSourceFilter.IsAllowed(null, allowed), Is.False);
    }

    [Test]
    public void 空白项忽略_非法项与单段数字单独列出()
    {
        var (allowed, invalid) = CallbackSourceFilter.Parse(new[] { " 10.0.0.5 ", "", "  ", null, "13", "rcs-host", "::1" });

        Assert.Multiple(() =>
        {
            Assert.That(allowed.Select(a => a.ToString()), Is.EqualTo(new[] { "10.0.0.5", "::1" }));
            Assert.That(invalid, Is.EqualTo(new[] { "13", "rcs-host" }), "\"13\" 会被 TryParse 当成 0.0.0.13，必须判非法");
        });
    }

    [Test]
    public void 白名单为空_failClosed拒绝全部()
    {
        Assert.That(CallbackSourceFilter.IsRejected(IPAddress.Parse("10.9.9.9"), Array.Empty<IPAddress>()), Is.True);
    }

    [Test]
    public void 白名单非空_名单外与来源未知均拒绝()
    {
        var (allowed, _) = CallbackSourceFilter.Parse(new[] { "192.168.250.13" });

        Assert.Multiple(() =>
        {
            Assert.That(CallbackSourceFilter.IsRejected(IPAddress.Parse("192.168.250.13"), allowed), Is.False);
            Assert.That(CallbackSourceFilter.IsRejected(IPAddress.Parse("192.168.250.14"), allowed), Is.True);
            Assert.That(CallbackSourceFilter.IsRejected(null, allowed), Is.True);
        });
    }

    [Test]
    public void 现场白名单不含环回_本机探针仍放行()
    {
        var (allowed, _) = CallbackSourceFilter.Parse(new[] { "192.168.250.13" });
        var withProbe = CallbackSourceFilter.EnsureLocalProbeAllowed(allowed);

        Assert.Multiple(() =>
        {
            Assert.That(CallbackSourceFilter.IsRejected(IPAddress.Loopback, withProbe), Is.False);
            Assert.That(CallbackSourceFilter.IsRejected(IPAddress.IPv6Loopback, withProbe), Is.False);
            Assert.That(CallbackSourceFilter.IsRejected(IPAddress.Parse("192.168.250.13"), withProbe), Is.False);
            Assert.That(CallbackSourceFilter.IsRejected(IPAddress.Parse("192.168.250.99"), withProbe), Is.True);
        });
    }

    [Test]
    public void 白名单为空_EnsureLocalProbe不改failClosed()
    {
        var empty = Array.Empty<IPAddress>();
        var withProbe = CallbackSourceFilter.EnsureLocalProbeAllowed(empty);

        Assert.Multiple(() =>
        {
            Assert.That(withProbe, Is.Empty);
            Assert.That(CallbackSourceFilter.IsRejected(IPAddress.Parse("10.9.9.9"), withProbe), Is.True);
        });
    }
}
