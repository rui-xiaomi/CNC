using System.Reflection;
using CncLoader.Communication.DependencyInjection;
using CncLoader.Communication.Rcs;
using CncLoader.Communication.State;
using CncLoader.Core.Abstractions;
using CncLoader.Core.DependencyInjection;
using CncLoader.Core.Rcs;
using CncLoader.Core.State;
using CncLoader.Data;
using CncLoader.Data.DependencyInjection;
using CncLoader.Data.Repositories;
using CncLoader.UI.DependencyInjection;
using CncLoader.UI.ViewModels.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CncLoader.Core.Tests.Routing;

/// <summary>
/// P0-5 最终审计：生产 DI 描述符与构造依赖（不 Build 完整 WPF/设备容器）。
/// </summary>
[TestFixture]
public sealed class ProductionRoutingGateWiringTests
{
    [Test]
    public void ProductionDescriptors_Register_RealGateImplementations_Only()
    {
        var services = BuildProductionDescriptors();

        Assert.Multiple(() =>
        {
            AssertUniqueImplementation<IRcsTaskService>(services, typeof(RcsTaskService));
            AssertUniqueImplementation<IManagedDispatchRouteResolver>(services, typeof(ManagedDispatchRouteResolver));
            AssertUniqueImplementation<IRoutingAvailabilityValidator>(services, typeof(RoutingAvailabilityValidator));
            AssertUniqueImplementation<IRcsTaskStore>(services, typeof(RcsTaskStore));
            AssertUniqueImplementation<IEquipmentRoutingStore>(services, typeof(EquipmentRoutingStore));
            AssertUniqueImplementation<ILocationMapRoutingStore>(services, typeof(LocationMapRoutingStore));
            AssertUniqueImplementation<IFrameRoutingStore>(services, typeof(FrameRoutingStore));
            AssertUniqueImplementation<IChangeFrameOrchestrator>(services, typeof(ChangeFrameOrchestrator));
            AssertUniqueImplementation<IInventoryService>(services, typeof(InventoryService));

            var client = services.Where(d => d.ServiceType == typeof(IRcsClient)).ToList();
            Assert.That(client, Has.Count.EqualTo(1), "IRcsClient 须唯一注册");
            Assert.That(client[0].ImplementationFactory, Is.Not.Null,
                "生产 IRcsClient 经 factory 构造 RcsClient");
            Assert.That(client[0].Lifetime, Is.EqualTo(ServiceLifetime.Singleton));

            Assert.That(services.Any(d =>
                    d.ImplementationType == typeof(RcsTaskTracker)
                    || (d.ImplementationFactory is not null
                        && d.ServiceType.Name.Contains("IHostedService", StringComparison.Ordinal))),
                Is.True, "须注册 HostedService（含 Tracker）");
            // HostedService 注册形态：AddHostedService<RcsTaskTracker> → ServiceDescriptor
            Assert.That(services.Any(d =>
                    d.ImplementationType == typeof(RcsTaskTracker)
                    || d.ServiceType == typeof(RcsTaskTracker)),
                Is.True,
                "RcsTaskTracker 须出现在描述符中");
            Assert.That(services.Any(d => d.ImplementationType == typeof(PositionScheduler)
                                          || d.ServiceType == typeof(IPositionScheduler)),
                Is.True);
            Assert.That(services.Any(d => d.ServiceType == typeof(RcsViewModel)), Is.True);
        });
    }

    [Test]
    public void ProductionDescriptors_Have_NoForbiddenTestSubstitutes()
    {
        var services = BuildProductionDescriptors();
        var implTypes = services
            .Select(d => d.ImplementationType)
            .Where(t => t is not null)
            .Cast<Type>()
            .ToList();

        Assert.Multiple(() =>
        {
            Assert.That(implTypes, Does.Not.Contain(typeof(TracingTaskService)));
            Assert.That(implTypes.Any(t => t.Name.Contains("AlwaysAvailable", StringComparison.OrdinalIgnoreCase)),
                Is.False);
            Assert.That(implTypes.Any(t => t.Name.StartsWith("Fake", StringComparison.OrdinalIgnoreCase)),
                Is.False);
            Assert.That(implTypes.Any(t => t.Name.StartsWith("Tracing", StringComparison.OrdinalIgnoreCase)),
                Is.False);
            Assert.That(implTypes.Any(t => t.Name.StartsWith("Noop", StringComparison.OrdinalIgnoreCase)),
                Is.False);
            Assert.That(implTypes.Any(t => t.Name.StartsWith("Stub", StringComparison.OrdinalIgnoreCase)),
                Is.False);
            Assert.That(implTypes.Any(t => t.Name.StartsWith("Mock", StringComparison.OrdinalIgnoreCase)),
                Is.False);
        });
    }

    [Test]
    public void GateConstructors_Require_NonOptional_GateDependencies()
    {
        Assert.Multiple(() =>
        {
            AssertNoOptionalParameters(typeof(RcsTaskService));
            AssertNoOptionalParameters(typeof(ManagedDispatchRouteResolver));
            AssertNoOptionalParameters(typeof(RoutingAvailabilityValidator));
            AssertNoOptionalParameters(typeof(RcsTaskTracker));
            AssertNoOptionalParameters(typeof(ChangeFrameOrchestrator));
            AssertNoOptionalParameters(typeof(InventoryService));
            AssertNoOptionalParameters(typeof(RcsViewModel));

            // PositionScheduler：门禁依赖必填；writeHook/notifier 为非路由可选（模拟器/测试），不得扩大到 Validator
            AssertRequiredParameter(typeof(PositionScheduler), typeof(IRcsTaskService));
            AssertRequiredParameter(typeof(PositionScheduler), typeof(IRoutingAvailabilityValidator));
            AssertOptionalAllowlist(typeof(PositionScheduler),
                typeof(IPlcWriteHook), typeof(RcsCallbackNotifier));
        });
    }

    [Test]
    public void Singletons_DoNot_Take_DbContext_Directly()
    {
        var services = BuildProductionDescriptors();
        var singletons = services
            .Where(d => d.Lifetime == ServiceLifetime.Singleton && d.ImplementationType is not null)
            .Select(d => d.ImplementationType!)
            .Distinct()
            .ToList();

        Assert.Multiple(() =>
        {
            foreach (var t in singletons)
            {
                foreach (var ctor in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                {
                    foreach (var p in ctor.GetParameters())
                    {
                        Assert.That(p.ParameterType == typeof(CncDbContext), Is.False,
                            $"{t.Name} Singleton 不得直接注入 DbContext（应用 IDbContextFactory）");
                    }
                }
            }

            var storeCtor = typeof(RcsTaskStore).GetConstructors()[0];
            Assert.That(storeCtor.GetParameters().Select(p => p.ParameterType),
                Does.Contain(typeof(IDbContextFactory<CncDbContext>)));
        });
    }

    [Test]
    public void RegistrationOrder_LastWins_Still_RealImplementations()
    {
        var services = new ServiceCollection();
        services.AddCncCore();
        services.AddCncData();
        services.AddCncCommunication();
        services.AddCncUi();

        Assert.Multiple(() =>
        {
            AssertLastImplementation<IRcsTaskService>(services, typeof(RcsTaskService));
            AssertLastImplementation<IManagedDispatchRouteResolver>(services, typeof(ManagedDispatchRouteResolver));
            AssertLastImplementation<IRoutingAvailabilityValidator>(services, typeof(RoutingAvailabilityValidator));
            Assert.That(services.Count(d => d.ServiceType == typeof(IRcsTaskService)), Is.EqualTo(1));
            Assert.That(services.Count(d => d.ServiceType == typeof(IRcsClient)), Is.EqualTo(1));
        });
    }

    [Test]
    public void ProbeSeams_AreInternal_NotPublicProductionApi()
    {
        var probe = typeof(RcsTaskTracker).GetMethod("ProbeAutoRedoOnceAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.That(probe, Is.Not.Null);
        Assert.That(probe!.IsPublic, Is.False, "Probe 不得成为公开生产 API");
    }

    private static ServiceCollection BuildProductionDescriptors()
    {
        var services = new ServiceCollection();
        services.AddCncCore();
        services.AddCncData();
        services.AddCncCommunication();
        services.AddCncUi();
        return services;
    }

    private static void AssertUniqueImplementation<TService>(IServiceCollection services, Type expectedImpl)
    {
        var matches = services.Where(d => d.ServiceType == typeof(TService)).ToList();
        Assert.That(matches, Has.Count.EqualTo(1), $"{typeof(TService).Name} 注册数");
        Assert.That(matches[0].ImplementationType, Is.EqualTo(expectedImpl),
            $"{typeof(TService).Name} → {expectedImpl.Name}");
        Assert.That(matches[0].Lifetime, Is.EqualTo(ServiceLifetime.Singleton));
    }

    private static void AssertLastImplementation<TService>(IServiceCollection services, Type expectedImpl)
    {
        var last = services.Last(d => d.ServiceType == typeof(TService));
        Assert.That(last.ImplementationType, Is.EqualTo(expectedImpl));
    }

    private static void AssertNoOptionalParameters(Type type)
    {
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.That(ctors, Is.Not.Empty, $"{type.Name} 须有公共构造");
        foreach (var ctor in ctors)
        {
            foreach (var p in ctor.GetParameters())
            {
                Assert.That(p.HasDefaultValue, Is.False,
                    $"{type.Name}.{p.Name} 不得可选默认（防 null Validator/Resolver）");
            }
        }
    }

    private static void AssertRequiredParameter(Type type, Type parameterType)
    {
        var p = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .FirstOrDefault(x => x.ParameterType == parameterType
                                 || Nullable.GetUnderlyingType(x.ParameterType) == parameterType);
        Assert.That(p, Is.Not.Null, $"{type.Name} 须注入 {parameterType.Name}");
        Assert.That(p!.HasDefaultValue, Is.False, $"{type.Name}.{parameterType.Name} 不得可选");
    }

    private static void AssertOptionalAllowlist(Type type, params Type[] allowedOptional)
    {
        var allowed = new HashSet<Type>(allowedOptional);
        foreach (var p in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                     .SelectMany(c => c.GetParameters())
                     .Where(x => x.HasDefaultValue))
        {
            var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
            Assert.That(allowed, Does.Contain(t),
                $"{type.Name}.{p.Name}:{t.Name} 可选但未在审计允许名单（门禁相关不得可选）");
        }
    }
}
