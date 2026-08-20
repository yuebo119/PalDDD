using Microsoft.Extensions.DependencyInjection;
using PalDDD.Core;
using PalDDD.CQRS;
using PalDDD.Messaging;
using PalDDD.Serialization;

namespace PalDDD.DependencyInjection.Tests;

public sealed class ServiceRegistrationTests
{
    [Test]
    public async Task AddPalDDD_DoesNotRegisterConcreteSerializerAdapter()
    {
        var services = new ServiceCollection();

        services.AddPalDDD();

        using var provider = services.BuildServiceProvider();
        await Assert.That(provider.GetService<IMessageSerializer>()).IsNull();
    }

    [Test]
    public async Task AddPalDDD_RegistersDispatcherAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddPalDDD();
        using var provider = services.BuildServiceProvider();

        var d1 = provider.GetRequiredService<Dispatcher>();
        var d2 = provider.GetRequiredService<Dispatcher>();

        await Assert.That(d1).IsSameReferenceAs(d2);
    }

    [Test]
    public async Task AddPalDDD_RegistersNullMessageBrokerAsSingleton()
    {
        var services = new ServiceCollection();
        services.AddPalDDD();
        using var provider = services.BuildServiceProvider();

        var broker = provider.GetRequiredService<IMessageBroker>();
        await Assert.That(broker).IsNotNull();
        await Assert.That(broker).IsTypeOf<NullMessageBroker>();
    }

    [Test]
    public async Task AddPalCoreStack_RegistersCoreAndPipelineBehaviors()
    {
        var services = new ServiceCollection();

        services.AddPalCoreStack();

        using var provider = services.BuildServiceProvider();
        await Assert.That(provider.GetRequiredService<Dispatcher>()).IsNotNull();
        await Assert.That(provider.GetRequiredService<IDomainEventDispatcher>()).IsNotNull();

        var pipelineDescriptors = services
            .Where(sd => sd.ServiceType.IsGenericType
                         && sd.ServiceType.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>))
            .ToList();
        await Assert.That(pipelineDescriptors.Count).IsEqualTo(2);
    }

    [Test]
    public async Task AddPalFullStack_EqualsCoreStackWithoutInfrastructureAdapters()
    {
        var services = new ServiceCollection();

        services.AddPalFullStack();

        using var provider = services.BuildServiceProvider();
        await Assert.That(provider.GetRequiredService<Dispatcher>()).IsNotNull();
        await Assert.That(provider.GetRequiredService<IMessageBroker>()).IsTypeOf<NullMessageBroker>();
        await Assert.That(provider.GetService<IMessageSerializer>()).IsNull();
    }

    [Test]
    public async Task AddPalEventHandler_RegistersHandlerAsScoped()
    {
        var services = new ServiceCollection();
        services.AddPalEventHandler<TestDomainEvent, TestDomainEventHandler>();
        using var provider = services.BuildServiceProvider();

        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<TestDomainEventHandler>();
        var firstViaInterface = firstScope.ServiceProvider.GetRequiredService<IEventHandler<TestDomainEvent>>();
        var second = secondScope.ServiceProvider.GetRequiredService<TestDomainEventHandler>();

        // 十七轮契约变更：TryAddEnumerable 实现类型描述符（多 handler 聚合必需）不再
        // "转发到具体注册"——接口与具体解析是同 scope 内两个同类型实例（MS.DI 标准行为）
        await Assert.That(firstViaInterface).IsTypeOf<TestDomainEventHandler>();
        await Assert.That(first).IsNotSameReferenceAs(second);
    }

    /// <summary>AddPalEventHandler 同时注册泛型和非泛型接口</summary>
    [Test]
    public async Task AddPalEventHandler_RegistersBothGenericAndNonGenericInterfaces()
    {
        var services = new ServiceCollection();
        services.AddPalEventHandler<TestDomainEvent, TestDomainEventHandler>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var genericHandler = scope.ServiceProvider.GetRequiredService<IEventHandler<TestDomainEvent>>();
        var nonGenericHandler = scope.ServiceProvider.GetRequiredService<IEventHandler>();

        // 十七轮契约变更：同上——两接口各自经实现类型描述符解析（同类型不同实例）
        await Assert.That(genericHandler).IsTypeOf<TestDomainEventHandler>();
        await Assert.That(nonGenericHandler).IsTypeOf<TestDomainEventHandler>();
    }

    /// <summary>AddPalCommandHandler 注册 Handler + 接口（作用域）</summary>
    [Test]
    public async Task AddPalCommandHandler_RegistersHandlerAndInterfaceAsScoped()
    {
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalCommandHandler<TestCommand, string, TestCommandHandler>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        // 两种解析路径均可正常工作
        var handler = scope.ServiceProvider.GetRequiredService<TestCommandHandler>();
        var viaInterface = scope.ServiceProvider.GetRequiredService<ICommandHandler<TestCommand, string>>();

        await Assert.That(handler).IsNotNull();
        await Assert.That(viaInterface).IsNotNull();
        await Assert.That(viaInterface).IsTypeOf<TestCommandHandler>();

        // 不同作用域返回不同实例
        using var scope2 = provider.CreateScope();
        var handler2 = scope2.ServiceProvider.GetRequiredService<TestCommandHandler>();
        await Assert.That(handler).IsNotSameReferenceAs(handler2);
    }

    /// <summary>AddPalQueryHandler 注册 Handler + 接口（作用域）</summary>
    [Test]
    public async Task AddPalQueryHandler_RegistersHandlerAndInterfaceAsScoped()
    {
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalQueryHandler<TestQuery, int, TestQueryHandler>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var handler = scope.ServiceProvider.GetRequiredService<TestQueryHandler>();
        var viaInterface = scope.ServiceProvider.GetRequiredService<IQueryHandler<TestQuery, int>>();

        await Assert.That(handler).IsNotNull();
        await Assert.That(viaInterface).IsNotNull();
        await Assert.That(viaInterface).IsTypeOf<TestQueryHandler>();
    }

    /// <summary>AddPalPipelineBehaviors 注册两个开放泛型管道行为到服务集合</summary>
    [Test]
    public async Task AddPalPipelineBehaviors_RegistersTwoPipelineBehaviorDescriptors()
    {
        var services = new ServiceCollection();
        services.AddPalPipelineBehaviors();

        // 两个开放泛型注册：ValidationBehavior + LoggingBehavior
        var pipelineDescriptors = services
            .Where(sd => sd.ServiceType.IsGenericType
                         && sd.ServiceType.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>))
            .ToList();

        await Assert.That(pipelineDescriptors.Count).IsEqualTo(2);
    }

    [Test]
    public async Task AddPalPipelineBehaviors_DoubleCall_DoesNotDuplicateRegistrations()
    {
        // P3 回归（十八轮验证轮 D）：哨兵防重——双调后管道注册仍为 2（不翻倍）
        var services = new ServiceCollection();
        services.AddPalPipelineBehaviors();
        services.AddPalPipelineBehaviors();

        var pipelineDescriptors = services
            .Where(sd => sd.ServiceType.IsGenericType
                         && sd.ServiceType.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>))
            .ToList();

        await Assert.That(pipelineDescriptors.Count).IsEqualTo(2);
    }

    /// <summary>TryAdd 语义 — 重复调用 AddPalDDD 不引发注册冲突</summary>
    [Test]
    public async Task AddPalDDD_Idempotent_DoubleRegistrationDoesNotThrow()
    {
        var services = new ServiceCollection();

        services.AddPalDDD();
        // 第二次调用不应抛异常（TryAddSingleton 保证幂等）
        services.AddPalDDD();

        using var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<Dispatcher>();
        await Assert.That(dispatcher).IsNotNull();
    }

    // ─── AOT 闭合注册测试（AotCannotCreateGenericValueType 修复）───

    /// <summary>回归：开放版在前 + AddPalCommandHandler 在后（旧代码升级）→ 收敛 2 个 behavior，不叠加成 4</summary>
    [Test]
    public async Task MixOpenThenClosed_ResolvesTwoBehaviors()
    {
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalPipelineBehaviors(); // 开放版（旧代码）
        services.AddPalCommandHandler<TestCommand, string, TestCommandHandler>(); // 自动闭合注册
        using var provider = services.BuildServiceProvider();

        var behaviors = provider.GetServices<IPipelineBehavior<TestCommand, string>>().ToArray();
        Console.WriteLine($"PROBE open-then-closed: {behaviors.Length} behaviors");
        await Assert.That(behaviors.Length).IsEqualTo(2); // 期望 2，若 4 则混用回归成立
    }

    /// <summary>回归：闭合在前 + 开放版在后 → 收敛 2 个 behavior，不叠加成 4</summary>
    [Test]
    public async Task MixClosedThenOpen_ResolvesTwoBehaviors()
    {
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalCommandHandler<TestCommand, string, TestCommandHandler>(); // 自动闭合注册
        services.AddPalPipelineBehaviors(); // 开放版（后调用）
        using var provider = services.BuildServiceProvider();

        var behaviors = provider.GetServices<IPipelineBehavior<TestCommand, string>>().ToArray();
        Console.WriteLine($"PROBE closed-then-open: {behaviors.Length} behaviors");
        await Assert.That(behaviors.Length).IsEqualTo(2);
    }

    [Test]
    public async Task AddPalPipelineBehaviors_ClosedGeneric_RegistersForRequestResponse()
    {
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalPipelineBehaviors<TestCommand, string>();
        using var provider = services.BuildServiceProvider();

        // 闭合注册：GetServices<IPipelineBehavior<TestCommand, string>>() 应解析到 2 个内置行为
        var behaviors = provider.GetServices<IPipelineBehavior<TestCommand, string>>().ToArray();
        await Assert.That(behaviors.Length).IsEqualTo(2);
    }

    [Test]
    public async Task AddPalPipelineBehaviors_ClosedGeneric_ValueTypeResponse_Resolves()
    {
        // 值类型响应（int）闭合注册——AOT 下走 TryCreateExact 不经值类型校验
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalPipelineBehaviors<TestQuery, int>();
        using var provider = services.BuildServiceProvider();

        var behaviors = provider.GetServices<IPipelineBehavior<TestQuery, int>>().ToArray();
        await Assert.That(behaviors.Length).IsEqualTo(2);
    }

    [Test]
    public async Task AddPalPipelineBehaviors_ClosedGeneric_MultipleCalls_DoesNotDuplicate()
    {
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalPipelineBehaviors<TestCommand, string>();
        services.AddPalPipelineBehaviors<TestCommand, string>(); // 重复调用
        using var provider = services.BuildServiceProvider();

        var behaviors = provider.GetServices<IPipelineBehavior<TestCommand, string>>().ToArray();
        await Assert.That(behaviors.Length).IsEqualTo(2); // TryAddEnumerable 去重
    }

    [Test]
    public async Task AddPalCommandHandler_RegistersClosedPipelineBehaviors()
    {
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalCommandHandler<TestCommand, string, TestCommandHandler>();
        using var provider = services.BuildServiceProvider();

        // AddPalCommandHandler 应自动闭合注册内置行为
        var behaviors = provider.GetServices<IPipelineBehavior<TestCommand, string>>().ToArray();
        await Assert.That(behaviors.Length).IsEqualTo(2);
    }

    [Test]
    public async Task AddPalQueryHandler_RegistersClosedPipelineBehaviors_ValueTypeResponse()
    {
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalQueryHandler<TestQuery, int, TestQueryHandler>();
        using var provider = services.BuildServiceProvider();

        // 值类型响应（int）也应自动闭合注册
        var behaviors = provider.GetServices<IPipelineBehavior<TestQuery, int>>().ToArray();
        await Assert.That(behaviors.Length).IsEqualTo(2);
    }

    private sealed class TestDomainEvent : DomainEvent;

    // ─── AOT 闭合注册测试（AotCannotCreateGenericValueType 修复 · 互斥检查替代哨兵）───

    /// <summary>回归：开放版在前 + AddPalCommandHandler 在后（旧代码升级）→ 收敛 2 个 behavior，不叠加成 4</summary>
    [Test]
    public async Task MixOpenThenClosed_ResolvesTwoBehaviors()
    {
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalPipelineBehaviors(); // 开放版（旧代码）
        services.AddPalCommandHandler<TestCommand, string, TestCommandHandler>(); // 自动闭合注册
        using var provider = services.BuildServiceProvider();

        var behaviors = provider.GetServices<IPipelineBehavior<TestCommand, string>>().ToArray();
        await Assert.That(behaviors.Length).IsEqualTo(2);
    }

    /// <summary>回归：闭合在前 + 开放版在后 → 收敛 2 个 behavior，不叠加成 4</summary>
    [Test]
    public async Task MixClosedThenOpen_ResolvesTwoBehaviors()
    {
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalCommandHandler<TestCommand, string, TestCommandHandler>(); // 自动闭合注册
        services.AddPalPipelineBehaviors(); // 开放版（后调用）
        using var provider = services.BuildServiceProvider();

        var behaviors = provider.GetServices<IPipelineBehavior<TestCommand, string>>().ToArray();
        await Assert.That(behaviors.Length).IsEqualTo(2);
    }

    [Test]
    public async Task AddPalPipelineBehaviors_ClosedGeneric_RegistersForRequestResponse()
    {
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalPipelineBehaviors<TestCommand, string>();
        using var provider = services.BuildServiceProvider();

        // 闭合注册：GetServices<IPipelineBehavior<TestCommand, string>>() 应解析到 2 个内置行为
        var behaviors = provider.GetServices<IPipelineBehavior<TestCommand, string>>().ToArray();
        await Assert.That(behaviors.Length).IsEqualTo(2);
    }

    [Test]
    public async Task AddPalPipelineBehaviors_ClosedGeneric_ValueTypeResponse_Resolves()
    {
        // 值类型响应（int）闭合注册——AOT 下走 TryCreateExact 不经值类型校验
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalPipelineBehaviors<TestQuery, int>();
        using var provider = services.BuildServiceProvider();

        var behaviors = provider.GetServices<IPipelineBehavior<TestQuery, int>>().ToArray();
        await Assert.That(behaviors.Length).IsEqualTo(2);
    }

    [Test]
    public async Task AddPalPipelineBehaviors_ClosedGeneric_MultipleCalls_DoesNotDuplicate()
    {
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalPipelineBehaviors<TestCommand, string>();
        services.AddPalPipelineBehaviors<TestCommand, string>(); // 重复调用
        using var provider = services.BuildServiceProvider();

        var behaviors = provider.GetServices<IPipelineBehavior<TestCommand, string>>().ToArray();
        await Assert.That(behaviors.Length).IsEqualTo(2); // TryAddEnumerable 去重
    }

    [Test]
    public async Task AddPalCommandHandler_RegistersClosedPipelineBehaviors()
    {
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalCommandHandler<TestCommand, string, TestCommandHandler>();
        using var provider = services.BuildServiceProvider();

        // AddPalCommandHandler 应自动闭合注册内置行为
        var behaviors = provider.GetServices<IPipelineBehavior<TestCommand, string>>().ToArray();
        await Assert.That(behaviors.Length).IsEqualTo(2);
    }

    [Test]
    public async Task AddPalQueryHandler_RegistersClosedPipelineBehaviors_ValueTypeResponse()
    {
        var services = new ServiceCollection();
        services.AddPalLogging();
        services.AddPalQueryHandler<TestQuery, int, TestQueryHandler>();
        using var provider = services.BuildServiceProvider();

        // 值类型响应（int）也应自动闭合注册
        var behaviors = provider.GetServices<IPipelineBehavior<TestQuery, int>>().ToArray();
        await Assert.That(behaviors.Length).IsEqualTo(2);
    }

    private sealed class TestDomainEventHandler : IEventHandler<TestDomainEvent>
    {
        public ValueTask HandleAsync(TestDomainEvent @event, CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class AnotherDomainEventHandler : IEventHandler<TestDomainEvent>
    {
        public ValueTask HandleAsync(TestDomainEvent @event, CancellationToken ct) => ValueTask.CompletedTask;
    }

    [Test]
    public async Task AddPalEventHandler_MultipleHandlersForSameEvent_AllResolved()
    {
        // P1 回归（十七轮）：八轮 TryAddScoped 防重曾把同事件第二个不同 handler 的接口注册
        // 静默吞掉（GetServices 只剩首个）——TryAddEnumerable 按 ServiceType+ImplementationType
        // 对去重后，多 handler 聚合恢复且同 handler 双调仍防重
        var services = new ServiceCollection();
        services.AddPalEventHandler<TestDomainEvent, TestDomainEventHandler>();
        services.AddPalEventHandler<TestDomainEvent, AnotherDomainEventHandler>();
        // 同 handler 双调——防重仍应生效（各接口只 1 份）
        services.AddPalEventHandler<TestDomainEvent, TestDomainEventHandler>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var genericHandlers = scope.ServiceProvider.GetServices<IEventHandler<TestDomainEvent>>().ToList();
        var nonGenericHandlers = scope.ServiceProvider.GetServices<IEventHandler>().ToList();

        await Assert.That(genericHandlers.Count).IsEqualTo(2);
        await Assert.That(nonGenericHandlers.Count(h => h is IEventHandler<TestDomainEvent>)).IsEqualTo(2);
        // 具体类型双调防重
        await Assert.That(scope.ServiceProvider.GetServices<TestDomainEventHandler>().Count()).IsEqualTo(1);
    }

    private sealed class TestCommand : ICommand<string>;

    private sealed class TestCommandHandler : ICommandHandler<TestCommand, string>
    {
        public ValueTask<string> HandleAsync(TestCommand command, CancellationToken ct)
            => ValueTask.FromResult("ok");
    }

    private sealed class TestQuery : IQuery<int>;

    private sealed class TestQueryHandler : IQueryHandler<TestQuery, int>
    {
        public ValueTask<int> HandleAsync(TestQuery query, CancellationToken ct)
            => ValueTask.FromResult(42);
    }
}
