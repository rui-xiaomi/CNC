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

    [Test]
    public void 现场回包_优先取Data里的task_id()
    {
        var id = RcsAckParser.TryReadAssignedTaskId(RcsFieldAck.GrabAck());
        Assert.That(id, Is.EqualTo(RcsFieldAck.TaskId));
    }

    [Test]
    public void 现场回包_不取request_id()
    {
        var id = RcsAckParser.TryReadAssignedTaskId(RcsFieldAck.GrabAck());
        Assert.Multiple(() =>
        {
            Assert.That(id, Is.EqualTo(RcsFieldAck.TaskId));
            Assert.That(id, Is.Not.EqualTo(RcsFieldAck.RequestId));
        });
    }

    [Test]
    public void Data为对象时取task_id()
    {
        var id = RcsAckParser.TryReadAssignedTaskId(
            """{"Success":true,"Data":{"values":[{"json_msg":{"success":true,"state":{"booking":{"id":"BOOK-1"},"detail":{"phases":[{"activity":{"description":{"activities":[{"description":{"task_id":"TASK-FROM-NEST"}}]}}}]}}}}]}}""");
        Assert.That(id, Is.EqualTo("TASK-FROM-NEST"));
    }

    [Test]
    public void 无task_id时回退booking_id()
    {
        var id = RcsAckParser.TryReadAssignedTaskId(
            """{"Success":true,"Data":{"values":[{"json_msg":{"success":true,"state":{"booking":{"id":"BOOK-ONLY"}}}}]}}""");
        Assert.That(id, Is.EqualTo("BOOK-ONLY"));
    }

    [Test]
    public void json_msg失败不取task_id()
    {
        var id = RcsAckParser.TryReadAssignedTaskId(
            """{"Success":true,"Data":{"values":[{"json_msg":{"success":false,"state":{"booking":{"id":"NOPE"},"detail":{"task_id":"NOPE"}}}}]}}""");
        Assert.That(id, Is.Null);
    }

    [Test]
    public void 空Data不取号()
    {
        Assert.That(RcsAckParser.TryReadAssignedTaskId("""{"Success":true,"Message":"发送成功！","Data":null}"""), Is.Null);
    }

    [Test]
    public void queryTask按本地ID批量IN()
    {
        var req = QueryTaskRequest.ForLocalIds(new[] { "L1-GB-20260911142538-9209", "L1-GB-20260911142539-9210" });
        var item = req.Condition.Conditions.Single();
        Assert.Multiple(() =>
        {
            Assert.That(item.Key, Is.EqualTo("Id"));
            Assert.That(item.Operator, Is.EqualTo("IN"));
            Assert.That(item.Value, Is.EqualTo("L1-GB-20260911142538-9209,L1-GB-20260911142539-9210"));
            Assert.That(req.PageSize, Is.EqualTo(10));
        });
    }

    [Test]
    public void 回包号与本地号可区分()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RcsTaskId.IsAssignedRemoteId("CNC_WMS_TASK_2_2026-09-11_0416355691"), Is.True);
            Assert.That(RcsTaskId.IsAssignedRemoteId("L1-GB-20260911142538-9209"), Is.False);
            Assert.That(RcsTaskId.IsAssignedRemoteId(null), Is.False);
        });
    }

}
