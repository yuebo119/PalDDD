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

        await Assert.That(first).IsSameReferenceAs(firstViaInterface);
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

        await Assert.That(genericHandler).IsSameReferenceAs(nonGenericHandler);
    }

    /// <summary>AddPalCommandHandler 注册 Handler + 接口（作用域）</summary>
    [Test]
    public async Task AddPalCommandHandler_RegistersHandlerAndInterfaceAsScoped()
    {
        var services = new ServiceCollection();
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

    private sealed class TestDomainEvent : DomainEvent;

    private sealed class TestDomainEventHandler : IEventHandler<TestDomainEvent>
    {
        public ValueTask HandleAsync(TestDomainEvent @event, CancellationToken ct) => ValueTask.CompletedTask;
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
