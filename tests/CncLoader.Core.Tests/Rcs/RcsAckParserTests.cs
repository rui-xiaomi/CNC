using CncLoader.Core.Rcs;

namespace CncLoader.Core.Tests.Rcs;

[TestFixture]
public sealed class RcsAckParserTests
{
    [Test]
    public void 文档事例_Success字符串true_Message成功()
    {
        var (ok, msg) = RcsAckParser.Parse("""{"Success": "true", "Message": "成功", "Data": ""}""");
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(msg, Is.EqualTo("成功"));
        });
    }

    [Test]
    public void 文档事例_queryTask布尔success()
    {
        var (ok, msg) = RcsAckParser.Parse("""{"pageIndex":1,"items":[],"success":true,"message":null}""");
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(msg, Is.Null);
        });
    }

    [Test]
    public void 现场_仅Message成功_无Success字段()
    {
        var (ok, msg) = RcsAckParser.Parse("""{"Message":"成功"}""");
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(msg, Is.EqualTo("成功"));
        });
    }

    [Test]
    public void 现场_Success数字1()
    {
        var (ok, _) = RcsAckParser.Parse("""{"Success":1,"Message":"成功"}""");
        Assert.That(ok, Is.True);
    }

    [Test]
    public void 明确失败优先于Message成功()
    {
        var (ok, msg) = RcsAckParser.Parse("""{"Success":"false","Message":"成功"}""");
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(msg, Is.EqualTo("成功"));
        });
    }

    [Test]
    public void code0视为成功()
    {
        var (ok, _) = RcsAckParser.Parse("""{"code":0,"message":"成功"}""");
        Assert.That(ok, Is.True);
    }

    [Test]
    public void 文档失败事例_找不到仓位()
    {
        var (ok, msg) = RcsAckParser.Parse("""{"Success":"false","Message":"找不到仓位信息！仓位码：601203","Data":""}""");
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(msg, Does.Contain("找不到仓位"));
        });
    }

    [Test]
    public void HTTP200业务失败仍算链路可达()
    {
        var r = new RcsResult(true, 200, false, "成功", "{}", "{}", null, 12);
        Assert.That(RcsAckParser.IsHttpReachable(r), Is.True);
    }

    [Test]
    public void HTTP非2xx不可达()
    {
        var r = RcsResult.Fail("{}", "连接拒绝", 0, 5);
        Assert.That(RcsAckParser.IsHttpReachable(r), Is.False);
    }
}
