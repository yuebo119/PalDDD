// ─────────────────────────────────────────────────────────────
// 🏗️ 全局 DI 注册 — AddPalDDD / AddPalOutbox / AddPalPipelineBehaviors 等
// ─────────────────────────────────────────────────────────────
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PalDDD.Core.Logging;
using PalDDD.DependencyInjection.Logging;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using ZLogger;

namespace PalDDD.DependencyInjection;

/// <summary>PalDDD 核心 DI 注册 — 只注册内存总线、CQRS 分发和默认空消息代理</summary>
/// <remarks>
/// 所有 Handler 通过显式注册 API 或源码生成器注册，<b>不使用程序集扫描</b>——100% Native AOT 兼容。
/// 具体序列化、持久化、Outbox/Inbox、Broker 和 ASP.NET Core 能力由对应适配包显式注册。
/// </remarks>
public static class ServiceRegistration
{
    /// <summary>注册 PalDDD 核心：事件总线 + 分发器 + 空消息代理 + 迭代事件派发</summary>
    public static IServiceCollection AddPalDDD(this IServiceCollection services)
    {
        services.TryAddSingleton<CQRS.Dispatcher>();
        services.TryAddScoped<Message.IDomainEventDispatcher, Message.IterativeDomainEventDispatcher>();
        services.TryAddSingleton<Message.IMessageBroker, Message.NullMessageBroker>();
        services.TryAddSingleton<Message.DomainEventDispatcherOptions>();

        services.TryAddSingleton<HandlerCollector>();
        services.AddHostedService<HandlerRegistrar>();

        return services;
    }

    /// <summary>注册 ByteAether.Ulid 统一 ID 生成器。</summary>
    /// <remarks>将 <see cref="Core.Identity.IPalIdGenerator"/> 注册为单例，提供给 DomainEvent、OutboxMessage 等核心类型使用。</remarks>
    public static IServiceCollection AddPalIdentity(this IServiceCollection services)
    {
        services.TryAddSingleton<Core.Identity.IPalIdGenerator, Core.Identity.ByteAetherUlidGenerator>();
        return services;
    }

    /// <summary>注册推荐的核心栈：<see cref="AddPalDDD"/> + <see cref="AddPalPipelineBehaviors"/> + <see cref="AddPalIdentity"/>。</summary>
    /// <remarks>只包含 AOT 安全的核心、CQRS、内存消息能力；序列化、持久化、Broker、ASP.NET Core 适配器仍由对应包显式注册。</remarks>
    public static IServiceCollection AddPalCoreStack(this IServiceCollection services)
        => services.AddPalDDD().AddPalPipelineBehaviors().AddPalIdentity();

    /// <summary>注册 PalDDD 默认栈；当前等价于 <see cref="AddPalCoreStack"/>。</summary>
    /// <remarks>为新用户提供低认知入口，同时不越过 Clean Architecture 边界自动引用基础设施适配器。</remarks>
    public static IServiceCollection AddPalFullStack(this IServiceCollection services)
        => services.AddPalCoreStack();

    /// <summary>添加常用管道行为（验证 + 日志）</summary>
    /// <remarks>
    /// 注册两个开放泛型管道行为：<br/>
    /// - <see cref="CQRS.ValidationBehavior{TRequest,TResponse}"/>：自动调用所有 IPalValidator<br/>
    /// - <see cref="CQRS.LoggingBehavior{TRequest,TResponse}"/>：编译时日志记录
    /// </remarks>
    public static IServiceCollection AddPalPipelineBehaviors(this IServiceCollection services)
    {
        services.AddScoped(typeof(CQRS.IPipelineBehavior<,>), typeof(CQRS.ValidationBehavior<,>));
        services.AddScoped(typeof(CQRS.IPipelineBehavior<,>), typeof(CQRS.LoggingBehavior<,>));
        return services;
    }

    /// <summary>注册 ZLogger + IPalLogger&lt;T&gt; 日志门面。</summary>
    /// <remarks>
    /// 清除已有 Provider，设置最低级别为 Information，添加 ZLogger 控制台 JSON 格式化器。<br/>
    /// 注册 <see cref="IPalLogger{T}"/> → <see cref="PalLogger{T}"/> 单例适配。
    /// </remarks>
    public static IServiceCollection AddPalLogging(this IServiceCollection services)
    {
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddZLoggerConsole(options => options.UseJsonFormatter());
        });
        services.TryAddSingleton(typeof(IPalLogger<>), typeof(PalLogger<>));
        return services;
    }

    // ═══════════════════════════════════════════════════════════════
    // 显式 Handler 注册 API — AOT 安全，零反射
    // typeof(T) 均为编译时常量，源码生成器自动调用这些方法
    // ═══════════════════════════════════════════════════════════════

    /// <summary>显式注册命令处理器（AOT 安全）</summary>
    /// <typeparam name="TCommand">命令类型</typeparam>
    /// <typeparam name="TResponse">响应类型</typeparam>
    /// <typeparam name="THandler">处理器类型</typeparam>
    public static IServiceCollection AddPalCommandHandler<TCommand, TResponse,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors
            | DynamicallyAccessedMemberTypes.Interfaces)]
    THandler>(this IServiceCollection services)
        where TCommand : CQRS.IRequest<TResponse>
        where THandler : class, CQRS.ICommandHandler<TCommand, TResponse>
    {
        services.TryAddScoped<THandler>();
        services.TryAddScoped<CQRS.ICommandHandler<TCommand, TResponse>, THandler>();
        // 注册标记：typeof(TCommand) 和 typeof(THandler) 均为编译时常量
        services.AddSingleton(new HandlerMarker(
            requestType: typeof(TCommand),
            handlerType: typeof(THandler),
            responseType: typeof(TResponse),
            executor: CQRS.Dispatcher.ExecutePipelineAsync<TCommand, TResponse, THandler>));
        return services;
    }

    /// <summary>显式注册查询处理器（AOT 安全）</summary>
    /// <typeparam name="TQuery">查询类型</typeparam>
    /// <typeparam name="TResponse">响应类型</typeparam>
    /// <typeparam name="THandler">处理器类型</typeparam>
    public static IServiceCollection AddPalQueryHandler<TQuery, TResponse,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors
            | DynamicallyAccessedMemberTypes.Interfaces)]
    THandler>(this IServiceCollection services)
        where TQuery : CQRS.IQuery<TResponse>
        where THandler : class, CQRS.IQueryHandler<TQuery, TResponse>
    {
        services.TryAddScoped<THandler>();
        services.TryAddScoped<CQRS.IQueryHandler<TQuery, TResponse>, THandler>();
        services.AddSingleton(new HandlerMarker(
            requestType: typeof(TQuery),
            handlerType: typeof(THandler),
            responseType: typeof(TResponse),
            executor: CQRS.Dispatcher.ExecutePipelineAsync<TQuery, TResponse, THandler>));
        return services;
    }

    /// <summary>显式注册领域事件处理器（AOT 安全）</summary>
    /// <remarks>事件处理器默认注册为 Scoped，允许处理器安全依赖仓储、DbContext 或 Unit of Work。</remarks>
    /// <typeparam name="TEvent">领域事件类型</typeparam>
    /// <typeparam name="THandler">处理器类型</typeparam>
    public static IServiceCollection AddPalEventHandler<TEvent,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
    THandler>(this IServiceCollection services)
        where TEvent : Core.DomainEvent
        where THandler : class, Message.IEventHandler<TEvent>
    {
        services.TryAddScoped<THandler>();
        services.AddScoped<Message.IEventHandler<TEvent>>(sp => sp.GetRequiredService<THandler>());
        // 注册到非泛型接口以便 IterativeDomainEventDispatcher 通过 IEnumerable<IEventHandler> 聚合
        services.AddScoped<Message.IEventHandler>(sp => sp.GetRequiredService<THandler>());
        return services;
    }
}

// ═══════════════════════════════════════════════════════════════
// 内部类型：Handler 标记收集器 + 启动注册器
// ═══════════════════════════════════════════════════════════════

/// <summary>Handler 类型映射标记 — 启动时由 HandlerRegistrar 消费</summary>
internal sealed class HandlerMarker
{
    public Type RequestType { get; }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)]
    public Type HandlerType { get; }

    public Type ResponseType { get; }

    public CQRS.RequestExecutor Executor { get; }

    public HandlerMarker(
        Type requestType,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type handlerType,
        Type responseType,
        CQRS.RequestExecutor executor)
    {
        RequestType = requestType;
        HandlerType = handlerType;
        ResponseType = responseType;
        Executor = executor;
    }
}

/// <summary>收集所有通过显式 API 注册的 Handler 标记 — 构造函数注入聚合所有标记</summary>
internal sealed class HandlerCollector
{
    public ImmutableArray<HandlerMarker> Markers { get; }

    public HandlerCollector(IEnumerable<HandlerMarker> markers)
    {
        Markers = markers.ToImmutableArray();
    }
}

/// <summary>启动时注册 Handler 到 Dispatcher — 零反射，仅消费编译时已知的类型标记</summary>
internal sealed class HandlerRegistrar : IHostedService
{
    private readonly CQRS.Dispatcher _dispatcher;
    private readonly HandlerCollector _collector;

    public HandlerRegistrar(CQRS.Dispatcher dispatcher, HandlerCollector collector)
    {
        _dispatcher = dispatcher;
        _collector = collector;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var marker in _collector.Markers)
        {
            _dispatcher.Register(marker.RequestType, marker.HandlerType, marker.ResponseType, marker.Executor);
        }

        _dispatcher.Freeze();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
