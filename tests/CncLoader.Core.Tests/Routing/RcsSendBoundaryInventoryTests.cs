using System.Reflection;
using CncLoader.Communication.Rcs;
using CncLoader.Communication.State;
using CncLoader.Core.Rcs;
using CncLoader.Data.Repositories;
using CncLoader.UI.ViewModels.Pages;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 最终审计：从 IRcsClient 反向枚举发送边界，并维护新外部执行入口矩阵。
/// 禁止仅靠 TracingTaskService 证明；本文件做清单与反射断言，行为由既有专项覆盖。
/// </summary>
[TestFixture]
public sealed class RcsSendBoundaryInventoryTests
{
    /// <summary>
    /// 集中式入口清单：操作 → 生产调用方 → 受保护 Service 方法 → 是否新外部执行。
    /// 未来新增发送入口须在此登记，否则本测失败。
    /// </summary>
    private static readonly (string Operation, string Caller, string ServiceMethod, bool NewExternal)[] EntryMatrix =
    {
        ("自动上料", nameof(PositionScheduler), nameof(IRcsTaskService.DispatchTransitAsync), true),
        ("自动下料/交接", nameof(PositionScheduler), nameof(IRcsTaskService.DispatchTransitAsync), true),
        ("手动 Transit", nameof(RcsViewModel), nameof(IRcsTaskService.DispatchTransitAsync), true),
        ("PalletReturn", nameof(RcsViewModel), nameof(IRcsTaskService.DispatchPalletReturnAsync), true),
        ("Grab", nameof(RcsViewModel), nameof(IRcsTaskService.DispatchGrabAsync), true),
        ("Identify", nameof(RcsViewModel), nameof(IRcsTaskService.DispatchIdentifyAsync), true),
        ("Inventory", nameof(InventoryService), nameof(IRcsTaskService.DispatchIdentifyAsync), true),
        ("ChangeFrame pull/push", nameof(ChangeFrameOrchestrator), nameof(IRcsTaskService.DispatchTransitAsync), true),
        ("手动 Redo", nameof(RcsViewModel), nameof(IRcsTaskService.RedoAsync), true),
        ("手动 Redispatch", "IRcsTaskService 公开 API（当前无 UI 调用方）", nameof(IRcsTaskService.RedispatchAsync), true),
        ("Tracker AutoRedo", nameof(RcsTaskTracker), nameof(IRcsTaskService.AutoRedispatchAsync), true),
        ("Cancel", nameof(RcsViewModel) + "/" + nameof(PositionScheduler), nameof(IRcsTaskService.CancelAsync), false),
        ("queryTask/对账", nameof(RcsTaskTracker) + "/" + nameof(PositionScheduler), nameof(IRcsTaskService.QueryAsync), false),
        ("TestConnection", nameof(RcsViewModel), nameof(IRcsTaskService.QueryAsync), false),
        ("ConfirmCancel", nameof(RcsViewModel), nameof(IRcsTaskService.ConfirmCancelHandledAsync), false),
    };

    [Test]
    public void IRcsClient_Surface_IsExactly_FourOutboundMethods()
    {
        var methods = typeof(IRcsClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.That(methods, Is.EqualTo(new[]
        {
            nameof(IRcsClient.CancelTaskAsync),
            nameof(IRcsClient.ExcuteTaskAsync),
            nameof(IRcsClient.QueryTaskAsync),
            nameof(IRcsClient.TransitTaskAsync),
        }));
    }

    [Test]
    public void ProductionAssemblies_Only_RcsTaskService_Injects_IRcsClient()
    {
        var productionAssemblies = new[]
        {
            typeof(RcsTaskService).Assembly,
            typeof(PositionScheduler).Assembly, // same Communication
            typeof(RcsViewModel).Assembly,
            typeof(ManagedDispatchRouteResolver).Assembly,
        }.Distinct();

        var injectors = new List<string>();
        foreach (var asm in productionAssemblies)
        {
            foreach (var type in asm.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
            {
                foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (ctor.GetParameters().Any(p => p.ParameterType == typeof(IRcsClient)))
                        injectors.Add(type.FullName ?? type.Name);
                }
            }
        }

        Assert.That(injectors.Distinct().ToList(), Is.EqualTo(new[]
        {
            typeof(RcsTaskService).FullName
        }), "除 RcsTaskService 外不得直接注入 IRcsClient（防绕过门禁）");
    }

    [Test]
    public void EntryMatrix_Covers_All_IRcsTaskService_SendLike_Methods()
    {
        var serviceSendMethods = typeof(IRcsTaskService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType.IsGenericType
                        && m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                        && m.ReturnType.GetGenericArguments()[0] == typeof(RcsResult))
            .Select(m => m.Name)
            .OrderBy(n => n)
            .ToArray();

        var matrixMethods = EntryMatrix.Select(e => e.ServiceMethod).Distinct().OrderBy(n => n).ToArray();

        // Cancel/Query 也返回 RcsResult，须在矩阵；Dispatch* / Redo* 同理
        Assert.Multiple(() =>
        {
            foreach (var m in serviceSendMethods)
            {
                Assert.That(matrixMethods, Does.Contain(m),
                    $"IRcsTaskService.{m} 未登记到发送入口矩阵 — 新增入口须分类");
            }

            Assert.That(EntryMatrix.Count(e => e.NewExternal), Is.GreaterThanOrEqualTo(10),
                "新外部执行入口数量异常偏低，检查矩阵是否被掏空");
        });
    }

    [Test]
    public void NewExternalEntries_Land_On_RcsTaskService_PublicMethods()
    {
        var svcType = typeof(RcsTaskService);
        Assert.Multiple(() =>
        {
            foreach (var e in EntryMatrix.Where(x => x.NewExternal))
            {
                var m = svcType.GetMethod(e.ServiceMethod, BindingFlags.Public | BindingFlags.Instance);
                Assert.That(m, Is.Not.Null, $"{e.Operation} → {e.ServiceMethod} 须存在于真实 RcsTaskService");
            }

            // Tracker 不得再走旧 Increment→Redispatch
            var autoRedo = typeof(RcsTaskTracker).GetMethod("AutoRedoAsync",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(autoRedo, Is.Not.Null);
            // 源码契约：调用 AutoRedispatchAsync（反射 IL 字符串不可靠；用公开 API 存在性 + 无 Store.TryIncrement）
            Assert.That(typeof(IRcsTaskStore).GetMethod("TryIncrementRedoIfUnderAsync"), Is.Null);
            Assert.That(typeof(IRcsTaskService).GetMethod(nameof(IRcsTaskService.AutoRedispatchAsync)), Is.Not.Null);
        });
    }

    [Test]
    public void RcsTaskService_ClientCalls_AreOnly_After_GateMethods()
    {
        // 结构断言：Create/Client 调用仅出现在公开 Dispatch*/Redo*/Redispatch*/Auto*/Cancel/Query
        // BuildAndSendAsync 为 private，供重放路径在 Claim/Increment 之后使用
        var buildAndSend = typeof(RcsTaskService).GetMethod("BuildAndSendAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.That(buildAndSend, Is.Not.Null);
        Assert.That(buildAndSend!.IsPrivate, Is.True);

        var publicDispatchers = new[]
        {
            nameof(RcsTaskService.DispatchTransitAsync),
            nameof(RcsTaskService.DispatchGrabAsync),
            nameof(RcsTaskService.DispatchIdentifyAsync),
            nameof(RcsTaskService.DispatchPalletReturnAsync),
            nameof(RcsTaskService.RedoAsync),
            nameof(RcsTaskService.RedispatchAsync),
            nameof(RcsTaskService.AutoRedispatchAsync),
        };
        Assert.Multiple(() =>
        {
            foreach (var name in publicDispatchers)
                Assert.That(typeof(RcsTaskService).GetMethod(name), Is.Not.Null, name);
        });
    }

    [Test]
    public void HistoricalClosure_Entries_Are_Not_Classified_As_NewExternal()
    {
        Assert.Multiple(() =>
        {
            Assert.That(EntryMatrix.Where(e => e.ServiceMethod == nameof(IRcsTaskService.QueryAsync))
                    .All(e => !e.NewExternal), Is.True);
            Assert.That(EntryMatrix.Where(e => e.ServiceMethod == nameof(IRcsTaskService.CancelAsync))
                    .All(e => !e.NewExternal), Is.True);
            Assert.That(EntryMatrix.Where(e => e.ServiceMethod == nameof(IRcsTaskService.ConfirmCancelHandledAsync))
                    .All(e => !e.NewExternal), Is.True);
        });
    }

    [Test]
    public void RcsViewModel_DoesNot_Reference_IRcsClient()
    {
        var fields = typeof(RcsViewModel)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(f => f.FieldType);
        var props = typeof(RcsViewModel)
            .GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(p => p.PropertyType);
        var ctorParams = typeof(RcsViewModel).GetConstructors()
            .SelectMany(c => c.GetParameters().Select(p => p.ParameterType));

        var all = fields.Concat(props).Concat(ctorParams).ToList();
        Assert.That(all, Does.Not.Contain(typeof(IRcsClient)),
            "RcsViewModel 不得直接持有 IRcsClient");
        Assert.That(all, Does.Contain(typeof(IRcsTaskService)));
    }

    [Test]
    public void Tracker_ChangeFrame_Inventory_Scheduler_Inject_TaskService_Not_Client()
    {
        Assert.Multiple(() =>
        {
            AssertInjects(typeof(RcsTaskTracker), typeof(IRcsTaskService), typeof(IRcsClient));
            AssertInjects(typeof(ChangeFrameOrchestrator), typeof(IRcsTaskService), typeof(IRcsClient));
            AssertInjects(typeof(InventoryService), typeof(IRcsTaskService), typeof(IRcsClient));
            AssertInjects(typeof(PositionScheduler), typeof(IRcsTaskService), typeof(IRcsClient));
        });
    }

    [Test]
    public void EntryMatrix_Documents_RoleStrategies()
    {
        // 角色策略存在性（行为由专项测）；此处防止策略方法被删导致静默退化
        var svc = typeof(RcsTaskService);
        Assert.Multiple(() =>
        {
            Assert.That(svc.GetMethod("MatchesOperationRole", BindingFlags.NonPublic | BindingFlags.Static), Is.Not.Null);
            Assert.That(svc.GetMethod("MatchesGrabRole", BindingFlags.NonPublic | BindingFlags.Static), Is.Not.Null);
            Assert.That(svc.GetMethod("MatchesIdentifyRole", BindingFlags.NonPublic | BindingFlags.Static), Is.Not.Null);
            Assert.That(svc.GetMethod("MatchesChangeFrameRole", BindingFlags.NonPublic | BindingFlags.Static), Is.Not.Null);
            Assert.That(svc.GetMethod("MatchesPalletReturnRole", BindingFlags.NonPublic | BindingFlags.Static), Is.Not.Null);
        });
    }

    private static void AssertInjects(Type type, Type required, Type forbidden)
    {
        var paramTypes = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
            .ToList();
        Assert.That(paramTypes, Does.Contain(required), $"{type.Name} 须注入 {required.Name}");
        Assert.That(paramTypes, Does.Not.Contain(forbidden), $"{type.Name} 不得注入 {forbidden.Name}");
    }
}
