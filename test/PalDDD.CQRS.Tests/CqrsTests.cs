using Microsoft.Extensions.DependencyInjection;
using PalDDD.Core;
using PalDDD.Core.Logging;

namespace PalDDD.CQRS.Tests;

// ─── 测试用命令/查询模型 ───

public sealed record CreateOrderCommand(string CustomerName, decimal Amount) : ICommand<Guid>;

public sealed record GetOrderQuery(Guid OrderId) : IQuery<string>;

public sealed record DeleteOrderCommand(Guid OrderId) : ICommand;

// ─── 测试用 Handler ───

public sealed class CreateOrderHandler : ICommandHandler<CreateOrderCommand, Guid>
{
    public ValueTask<Guid> HandleAsync(CreateOrderCommand command, CancellationToken ct)
        => new(Guid.NewGuid());
}

public sealed class GetOrderHandler : IQueryHandler<GetOrderQuery, string>
{
    public ValueTask<string> HandleAsync(GetOrderQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        return new($"Order:{query.OrderId}");
    }
}

public sealed class DeleteOrderHandler : ICommandHandler<DeleteOrderCommand, Unit>
{
    public ValueTask<Unit> HandleAsync(DeleteOrderCommand command, CancellationToken ct)
        => ValueTask.FromResult(new Unit());
}

public sealed class CancellableHandler : ICommandHandler<CreateOrderCommand, Guid>
{
    public async ValueTask<Guid> HandleAsync(CreateOrderCommand command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Delay(100, ct);
        return Guid.NewGuid();
    }
}

// ─── DIM 桥接测试 ───

public sealed class DimBridgeTests
{
    [Test]
    public async Task CommandHandler_NonGenericBridge()
    {
        var handler = new CreateOrderHandler();
        var cmd = new CreateOrderCommand("Test", 100m);

        var result = await ((IHandler)handler).HandleAsync(cmd, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result).IsTypeOf<Guid>();
    }

    [Test]
    public async Task QueryHandler_NonGenericBridge()
    {
        var handler = new GetOrderHandler();
        var query = new GetOrderQuery(Guid.NewGuid());
        var result = await ((IHandler)handler).HandleAsync(query, CancellationToken.None);

        await Assert.That(result).IsEqualTo($"Order:{query.OrderId}");
    }

    [Test]
    public async Task VoidCommandHandler_NonGenericBridge_ReturnsUnit()
    {
        var handler = new DeleteOrderHandler();
        var cmd = new DeleteOrderCommand(Guid.NewGuid());

        var result = await ((IHandler)handler).HandleAsync(cmd, CancellationToken.None);

        await Assert.That(result).IsTypeOf<Unit>();
    }
}

// ─── 请求接口测试 ───

public sealed class RequestInterfaceTests
{
    [Test]
    public async Task Command_ImplementsIRequest()
    {
        IRequest<Guid> cmd = new CreateOrderCommand("Test", 100m);
        await Assert.That(cmd).IsNotNull();
    }

    [Test]
    public async Task Query_ImplementsIRequest()
    {
        IRequest<string> q = new GetOrderQuery(Guid.NewGuid());
        await Assert.That(q).IsNotNull();
    }

    [Test]
    public async Task ICommand_ImplementsIRequestOfUnit()
    {
        IRequest<Unit> cmd = new DeleteOrderCommand(Guid.NewGuid());
        await Assert.That(cmd).IsNotNull();
    }

    [Test]
    public async Task AllRequests_ImplementIBaseRequest()
    {
        IBaseRequest cmd = new CreateOrderCommand("X", 0);
        IBaseRequest q = new GetOrderQuery(Guid.NewGuid());
        IBaseRequest voidCmd = new DeleteOrderCommand(Guid.NewGuid());

        await Assert.That(cmd).IsNotNull();
        await Assert.That(q).IsNotNull();
        await Assert.That(voidCmd).IsNotNull();
    }
}

// ─── 验证异常测试 ───

public sealed class PalValidationExceptionTests
{
    [Test]
    public async Task DefaultConstructor_HasEmptyErrors()
    {
        var ex = new PalValidationException();
        await Assert.That(ex!.Errors).IsEmpty();
        await Assert.That(ex!.Message).Contains("Validation failed");
    }

    [Test]
    public async Task MessageConstructor_HasEmptyErrors()
    {
        var ex = new PalValidationException("Custom message");
        await Assert.That(ex!.Errors).IsEmpty();
        await Assert.That(ex!.Message).IsEqualTo("Custom message");
    }

    [Test]
    public async Task InnerExceptionConstructor_PreservesInnerException()
    {
        var inner = new InvalidOperationException("inner");
        var ex = new PalValidationException("Outer", inner);
        await Assert.That(ex.InnerException).IsSameReferenceAs(inner);
    }

    [Test]
    public async Task ErrorsConstructor_PopulatesErrors()
    {
        var errors = new List<PalValidationError>
        {
            new("Name", "Required"),
            new("Age", "Must be positive")
        };
        var ex = new PalValidationException([.. errors]);
        await Assert.That(ex!.Errors.Length).IsEqualTo(2);
        await Assert.That(ex!.Errors[0].PropertyName).Contains("Name");
        await Assert.That(ex!.Errors[1].PropertyName).Contains("Age");
    }

    [Test]
    public async Task SingleErrorConstructor_ContainsSingleError()
    {
        var ex = new PalValidationException("Email", "Invalid format");
        await Assert.That(ex!.Errors).Count().IsEqualTo(1);
        await Assert.That(ex!.Errors[0].PropertyName).IsEqualTo("Email");
        await Assert.That(ex!.Errors[0].Message).IsEqualTo("Invalid format");
    }
}

// ─── 分发器测试 ───

public sealed class DispatcherTests
{
    private static ServiceProvider CreateProvider(
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<CreateOrderHandler>();
        services.AddSingleton<GetOrderHandler>();
        services.AddSingleton<DeleteOrderHandler>();
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task SendAsync_WithResponse_ReturnsCorrectType()
    {
        var sp = CreateProvider();
        var dispatcher = new Dispatcher(sp.GetRequiredService<IServiceScopeFactory>());
        dispatcher.Register<CreateOrderCommand, Guid, CreateOrderHandler>();

        var result = await dispatcher.SendAsync(new CreateOrderCommand("Test", 100m));

        await Assert.That(result).IsNotEqualTo(Guid.Empty);
    }

    [Test]
    public async Task SendAsync_VoidCommand_CompletesSuccessfully()
    {
        var sp = CreateProvider();
        var dispatcher = new Dispatcher(sp.GetRequiredService<IServiceScopeFactory>());
        dispatcher.Register<DeleteOrderCommand, Unit, DeleteOrderHandler>();

        await dispatcher.SendAsync(new DeleteOrderCommand(Guid.NewGuid()));
    }

    [Test]
    public async Task QueryAsync_ReturnsResult()
    {
        var sp = CreateProvider();
        var dispatcher = new Dispatcher(sp.GetRequiredService<IServiceScopeFactory>());
        dispatcher.Register<GetOrderQuery, string, GetOrderHandler>();
        var orderId = Guid.NewGuid();

        var result = await dispatcher.QueryAsync(new GetOrderQuery(orderId));

        await Assert.That(result).IsEqualTo($"Order:{orderId}");
    }

    [Test]
    public async Task UnregisteredRequest_ThrowsInvalidOperation()
    {
        var sp = CreateProvider();
        var dispatcher = new Dispatcher(sp.GetRequiredService<IServiceScopeFactory>());

        var ex = await Assert.That(() => dispatcher.SendAsync(new CreateOrderCommand("X", 0)).AsTask())
            .Throws<HandlerNotFoundException>();

        await Assert.That(ex!.Message).Contains("未找到请求类型");
        await Assert.That(ex!.Message).Contains(nameof(CreateOrderCommand));
        await Assert.That(ex.RequestType).IsEqualTo(typeof(CreateOrderCommand));
    }

    [Test]
    public async Task SendAsync_PassesCancellationToken()
    {
        var sp = CreateProvider();
        var dispatcher = new Dispatcher(sp.GetRequiredService<IServiceScopeFactory>());
        dispatcher.Register<CreateOrderCommand, Guid, CreateOrderHandler>();

        using var cts = new CancellationTokenSource();
        var result = await dispatcher.SendAsync(new CreateOrderCommand("Test", 100m), cts.Token);

        await Assert.That(result).IsNotEqualTo(Guid.Empty);
    }

    [Test]
    public async Task SendAsync_ShouldCancel_WhenTokenCancelled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<CancellableHandler>();
        var sp = services.BuildServiceProvider();

        var dispatcher = new Dispatcher(sp.GetRequiredService<IServiceScopeFactory>());
        dispatcher.Register<CreateOrderCommand, Guid, CancellableHandler>();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(() => dispatcher.SendAsync(new CreateOrderCommand("X", 0), cts.Token).AsTask())
            .Throws<OperationCanceledException>();
    }
}

// ─── 管道行为测试 ───

public sealed class PipelineBehaviorTests
{
    // ─── 验证行为测试 ───

    public sealed class AlwaysValidValidator : IPalValidator<CreateOrderCommand>
    {
        public PalValidationResult Validate(CreateOrderCommand instance) => PalValidationResult.Success();
    }

    public sealed class AlwaysInvalidValidator : IPalValidator<CreateOrderCommand>
    {
        public PalValidationResult Validate(CreateOrderCommand instance)
            => PalValidationResult.Failed("CustomerName", "Required");
    }

    [Test]
    public async Task ValidationBehavior_ValidRequest_Passes()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPalValidator<CreateOrderCommand>, AlwaysValidValidator>();
        services.AddSingleton<CreateOrderHandler>();
        var sp = services.BuildServiceProvider();

        var behavior = new ValidationBehavior<CreateOrderCommand, Guid>(
            sp.GetServices<IPalValidator<CreateOrderCommand>>());

        var cmd = new CreateOrderCommand("Test", 100m);
        var handler = sp.GetRequiredService<CreateOrderHandler>();

        var result = await behavior.HandleAsync(cmd, CancellationToken.None,
            () => handler.HandleAsync(cmd, CancellationToken.None));

        await Assert.That(result).IsNotEqualTo(Guid.Empty);
    }

    [Test]
    public async Task ValidationBehavior_InvalidRequest_ThrowsValidationException()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPalValidator<CreateOrderCommand>, AlwaysInvalidValidator>();
        var sp = services.BuildServiceProvider();

        var behavior = new ValidationBehavior<CreateOrderCommand, Guid>(
            sp.GetServices<IPalValidator<CreateOrderCommand>>());

        var cmd = new CreateOrderCommand("Test", 100m);

        var ex = await Assert.That(() => behavior.HandleAsync(cmd, CancellationToken.None,
            () => new ValueTask<Guid>(Guid.NewGuid())).AsTask()).Throws<PalValidationException>();

        await Assert.That(ex!.Errors).Count().IsEqualTo(1);
    }

    // ─── 日志行为测试 ───

    [Test]
    public async Task LoggingBehavior_NoOp_WhenLoggerNotAvailable()
    {
        // 测试日志行为在底层 Handler 抛异常时正确传播
        var behavior = new LoggingBehavior<CreateOrderCommand, Guid>(
            NullPalLogger<LoggingBehavior<CreateOrderCommand, Guid>>.Instance);

        var cmd = new CreateOrderCommand("Test", 100m);

        var ex = await Assert.That(() => behavior.HandleAsync(cmd, CancellationToken.None,
            () => throw new InvalidOperationException("Handler failed")).AsTask())
            .Throws<InvalidOperationException>();

        await Assert.That(ex!.Message).IsEqualTo("Handler failed");
    }

    // ─── 管道链测试 ───

    public sealed class CountingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public int BeforeCount { get; private set; }
        public int AfterCount { get; private set; }

        public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct, Func<ValueTask<TResponse>> next)
        {
            ArgumentNullException.ThrowIfNull(next);

            BeforeCount++;
            var result = await next();
            AfterCount++;
            return result;
        }
    }

    [Test]
    public async Task Dispatcher_PipelineBehaviors_ExecuteInOrder()
    {
        var services = new ServiceCollection();
        services.AddSingleton<CreateOrderHandler>();
        services.AddSingleton(typeof(IPipelineBehavior<,>), typeof(CountingBehavior<,>));
        var sp = services.BuildServiceProvider();

        var dispatcher = new Dispatcher(sp.GetRequiredService<IServiceScopeFactory>());
        dispatcher.Register<CreateOrderCommand, Guid, CreateOrderHandler>();

        var result = await dispatcher.SendAsync(new CreateOrderCommand("Test", 100m));

        await Assert.That(result).IsNotEqualTo(Guid.Empty);
        // 验证管道行为确实被执行（通过 CountingBehavior 计数验证）
        var behaviorObj = sp.GetServices(typeof(IPipelineBehavior<CreateOrderCommand, Guid>)).First();
        await Assert.That(behaviorObj).IsTypeOf<CountingBehavior<CreateOrderCommand, Guid>>();
        var behavior = (CountingBehavior<CreateOrderCommand, Guid>)behaviorObj!;
        await Assert.That(behavior.BeforeCount).IsEqualTo(1);
        await Assert.That(behavior.AfterCount).IsEqualTo(1);
    }

    [Test]
    public async Task PipelineBehavior_NonGenericBridge_Works()
    {
        // 通过非泛型接口调用泛型行为
        var services = new ServiceCollection();
        services.AddSingleton(typeof(IPipelineBehavior<,>), typeof(CountingBehavior<,>));
        var sp = services.BuildServiceProvider();

        var behaviors = sp.GetServices(typeof(IPipelineBehavior<CreateOrderCommand, Guid>));
        await Assert.That(behaviors).IsNotEmpty();

        await Assert.That(behaviors.First()).IsTypeOf<CountingBehavior<CreateOrderCommand, Guid>>();
    }
}
